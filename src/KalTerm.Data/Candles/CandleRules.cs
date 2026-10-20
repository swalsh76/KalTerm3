using System.Globalization;
using System.Text;

namespace KalTerm.Data.Candles;

/// <summary>Which markets a stored candle interval covers.</summary>
public enum CandleScope
{
    /// <summary>Every market that traded or ticked in the bucket.</summary>
    All,

    /// <summary>Only the markets on the watch list.</summary>
    Watched,
}

/// <summary>One stored interval: how long a bucket is, and which markets it is built for.</summary>
/// <param name="IntervalSeconds">The bucket length: 60, 300, 900, 3600, 14400 or 86400.</param>
/// <param name="Scope">Which markets.</param>
public sealed record CandleRule(int IntervalSeconds, CandleScope Scope)
{
    /// <summary>The interval as people write it (<c>1m</c>, <c>1h</c>, <c>1d</c>).</summary>
    public string IntervalText => CandleRules.IntervalText(IntervalSeconds);

    /// <summary>The rule as it is written in configuration: <c>1h=all</c>.</summary>
    public override string ToString() => $"{IntervalText}={(Scope == CandleScope.All ? "all" : "watched")}";
}

/// <summary>The configuration of the stored intervals (<c>Ingest:Candles</c>), for example <c>1d=all;1h=all;1m=watched</c>, and the intervals a client may ask for.</summary>
public static class CandleRules
{
    /// <summary>The intervals that may be stored: they divide a day, so a bucket never straddles midnight.</summary>
    public static readonly IReadOnlyList<int> Storable = [60, 300, 900, 3600, 14400, 86400];

    /// <summary>The shortest interval a client may ask for, in seconds.</summary>
    public const int MinRequestSeconds = 60;

    /// <summary>The longest interval a client may ask for other than a week, in seconds.</summary>
    public const int MaxRequestSeconds = 86400;

    /// <summary>One week in seconds: the only interval above a day.</summary>
    public const int WeekSeconds = 604800;

    /// <summary>What is stored unless configured otherwise: days and hours for every market, minutes for the watch list.</summary>
    public const string DefaultText = "1d=all;1h=all;1m=watched";

    /// <summary>The default rules.</summary>
    public static IReadOnlyList<CandleRule> Default { get; } = Parse(DefaultText);

    /// <summary>Reads a rule list such as <c>1d=all;1h=all;1m=watched</c>.</summary>
    /// <param name="text">The text; empty means nothing is stored.</param>
    /// <returns>The rules, coarsest first.</returns>
    /// <exception cref="FormatException">An interval cannot be stored, a scope is unknown, or an interval is given twice.</exception>
    public static IReadOnlyList<CandleRule> Parse(string? text)
    {
        var rules = new List<CandleRule>();
        foreach (var part in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !TryParseInterval(pieces[0], out var seconds) || !Storable.Contains(seconds))
            {
                throw new FormatException($"'{part}' is not a stored candle interval and scope; use for example 1h=all (intervals: {string.Join(", ", Storable.Select(IntervalText))})");
            }

            var scope = pieces[1].ToLowerInvariant() switch
            {
                "all" => CandleScope.All,
                "watched" => CandleScope.Watched,
                _ => throw new FormatException($"'{pieces[1]}' is not a candle scope; use all or watched"),
            };
            if (rules.Any(r => r.IntervalSeconds == seconds))
            {
                throw new FormatException($"the candle interval {pieces[0]} is given twice");
            }

            rules.Add(new CandleRule(seconds, scope));
        }

        return [.. rules.OrderByDescending(r => r.IntervalSeconds)];
    }

    /// <summary>Writes rules the way <see cref="Parse"/> reads them.</summary>
    /// <param name="rules">The rules.</param>
    /// <returns>The text.</returns>
    public static string Format(IEnumerable<CandleRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.Join(';', rules.Select(r => r.ToString()));
    }

    /// <summary>Reads an interval a person or a client writes: <c>15m</c>, <c>4h</c>, <c>1d</c>, <c>1w</c>, or a bare number of minutes.</summary>
    /// <param name="text">The text.</param>
    /// <param name="seconds">The length in seconds.</param>
    /// <returns>Whether it is a whole number of minutes, hours, days or one week.</returns>
    public static bool TryParseInterval(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().ToLowerInvariant();
        var unit = trimmed[^1];
        var digits = char.IsDigit(unit) ? trimmed : trimmed[..^1];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || digits.Length > 6)
        {
            return false;
        }

        long total = char.IsDigit(unit) || unit == 'm' ? count * 60L : unit == 'h' ? count * 3600L : unit == 'd' ? count * 86400L : unit == 'w' ? count * 604800L : 0;
        if (total == 0 || total > WeekSeconds)
        {
            return false;
        }

        seconds = (int)total;
        return true;
    }

    /// <summary>Whether a client may ask for an interval: a whole number of minutes from 1 to 1,440, or exactly one week.</summary>
    /// <param name="seconds">The length in seconds.</param>
    /// <returns>True if allowed.</returns>
    public static bool IsRequestable(int seconds) => seconds == WeekSeconds || (seconds is >= MinRequestSeconds and <= MaxRequestSeconds && seconds % 60 == 0);

    /// <summary>The interval in the shortest exact form: <c>90s</c> never occurs, but <c>1w</c>, <c>1d</c>, <c>4h</c>, <c>15m</c> do.</summary>
    /// <param name="seconds">The length in seconds.</param>
    /// <returns>The text.</returns>
    public static string IntervalText(int seconds)
    {
        var text = new StringBuilder();
        text.Append(seconds % WeekSeconds == 0 ? $"{seconds / WeekSeconds}w" : seconds % 86400 == 0 ? $"{seconds / 86400}d" : seconds % 3600 == 0 ? $"{seconds / 3600}h" : $"{seconds / 60}m");
        return text.ToString();
    }

    /// <summary>
    /// The start of the bucket a moment falls in. Intervals up to a day count from the Unix epoch (so a day is a UTC day); a week counts from a Monday, because the epoch was a Thursday.
    /// </summary>
    /// <param name="moment">The moment (UTC).</param>
    /// <param name="seconds">The bucket length.</param>
    /// <returns>The bucket's start.</returns>
    public static DateTime BucketStart(DateTime moment, int seconds)
    {
        var origin = Origin(seconds);
        var elapsed = (long)(moment - origin).TotalSeconds;
        return origin.AddSeconds((elapsed / seconds) * seconds);
    }

    /// <summary>The moment buckets of an interval are counted from.</summary>
    /// <param name="seconds">The bucket length.</param>
    /// <returns>The Unix epoch, or the Monday after it for a week.</returns>
    public static DateTime Origin(int seconds) => seconds == WeekSeconds ? new DateTime(1970, 1, 5, 0, 0, 0, DateTimeKind.Utc) : DateTime.UnixEpoch;
}
