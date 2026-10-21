using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Client;

namespace KalTerm.Terminal.Services;

/// <summary>How values look to a person, in the time zone the person chose. The database and the API are UTC; the zone is only for showing. Same rules as the CLI.</summary>
/// <param name="zone">The zone times are shown in.</param>
internal sealed partial class Formatting(TimeZoneInfo zone)
{
    /// <summary>Shown where there is no value.</summary>
    public const string None = ValueText.None;

    private TimeZoneInfo _zone = zone;

    /// <summary>Raised when the zone changes, so shown times can be redrawn.</summary>
    public event Action? ZoneChanged;

    public TimeZoneInfo Zone
    {
        get => _zone;
        set
        {
            if (!ReferenceEquals(_zone, value))
            {
                _zone = value;
                ZoneChanged?.Invoke();
            }
        }
    }

    /// <summary>An API time (UTC) shown in the zone as <c>2026-10-18 22:30:15</c>, with milliseconds when asked (<c>22:30:15.250</c>).</summary>
    public string Moment(string? iso, bool milliseconds = false)
    {
        if (iso is null)
        {
            return None;
        }

        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? TimeZoneInfo.ConvertTime(parsed, _zone).DateTime.ToString(milliseconds ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : iso;
    }

    /// <summary>Like <see cref="Moment"/>, followed by the zone: <c>UTC</c>, or the offset that applied at that moment, for example <c>-06:00</c>.</summary>
    public string MomentWithZone(string? iso)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return Moment(iso);
        }

        var local = TimeZoneInfo.ConvertTime(parsed, _zone);
        var label = _zone.Equals(TimeZoneInfo.Utc) ? "UTC" : local.ToString("zzz", CultureInfo.InvariantCulture);
        return $"{Moment(iso)} {label}";
    }

    /// <summary>How long ago, coarsely: <c>45s ago</c>, <c>12m ago</c>, <c>5h ago</c>, <c>3d ago</c>.</summary>
    public static string Age(long? seconds) => ValueText.Age(seconds);

    /// <summary>A byte count as <c>264.0 MB</c>.</summary>
    public static string Size(long? bytes) => ValueText.Size(bytes);

    /// <summary>Dollars without trailing zeros but with at least two decimals: <c>0.560000</c> gives <c>0.56</c>, <c>1.000000</c> gives <c>1.00</c>. The API sends exact decimal text; this only shortens it. Same rules as the CLI.</summary>
    public static string Dollars(string? value) => ValueText.Dollars(value);

    /// <summary>Dollars with a sign for a change: <c>+0.02</c>, <c>-0.01</c>, and a plain <c>0.00</c> for none.</summary>
    public static string Signed(string? value) =>
        value is null ? None : value.StartsWith('-') || Dollars(value) is "0.00" ? Dollars(value) : "+" + Dollars(value);

    /// <summary>Contracts with thousands separators; a whole number loses its <c>.00</c>: <c>1234.00</c> gives <c>1,234</c>, <c>20.50</c> stays <c>20.50</c>.</summary>
    public static string Count(string? value) => ValueText.Count(value);

    /// <summary>The distance between two dollar prices as dollar text (<c>0.570000</c> minus <c>0.550000</c> is <c>0.020000</c>), worked out in whole millionths so nothing is rounded; <see langword="null"/> when either is missing or not a price.</summary>
    public static string? Difference(string? high, string? low)
    {
        if (Micros(high) is not { } top || Micros(low) is not { } bottom)
        {
            return null;
        }

        var micros = top - bottom;
        var magnitude = Math.Abs(micros);
        return string.Create(CultureInfo.InvariantCulture, $"{(micros < 0 ? "-" : string.Empty)}{magnitude / 1_000_000}.{magnitude % 1_000_000:D6}");
    }

    /// <summary>The middle of two dollar prices as dollar text, worked out in whole millionths (<c>0.540000</c> and <c>0.560000</c> give <c>0.550000</c>); <see langword="null"/> when either is missing or not a price.</summary>
    public static string? Midpoint(string? first, string? second)
    {
        if (Micros(first) is not { } a || Micros(second) is not { } b)
        {
            return null;
        }

        var micros = (a + b) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"{micros / 1_000_000}.{micros % 1_000_000:D6}");
    }

    /// <summary>Counts added up exactly (in hundredths): <c>10.50</c> and <c>2.25</c> give <c>12.75</c>. A count that is not a number with at most two decimals counts as nothing.</summary>
    /// <param name="counts">The counts, as the API sends them.</param>
    /// <returns>The sum as count text with two decimals.</returns>
    public static string SumCounts(IEnumerable<string?> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var total = 0L;
        foreach (var count in counts)
        {
            total += Hundredths(count) ?? 0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{total / 100}.{total % 100:D2}");
    }

    /// <summary>Count text as whole hundredths, or <see langword="null"/> when it is not a count with at most two decimals.</summary>
    public static long? Hundredths(string? value)
    {
        if (value is null || ValueText.DecimalText().Match(value) is not { Success: true } match || match.Groups["fraction"].Value.Length > 2 || match.Groups["sign"].Value.Length > 0)
        {
            return null;
        }

        return long.TryParse(match.Groups["whole"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) && whole <= 90_000_000_000_000L
            ? (whole * 100) + long.Parse(match.Groups["fraction"].Value.PadRight(2, '0'), NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Dollar text as whole millionths, or <see langword="null"/> when it is not dollar text with at most six decimals.</summary>
    private static long? Micros(string? value)
    {
        if (value is null || ValueText.DecimalText().Match(value) is not { Success: true } match || match.Groups["fraction"].Value.Length > 6)
        {
            return null;
        }

        if (!long.TryParse(match.Groups["whole"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) || whole > 9_000_000_000_000L)
        {
            return null;
        }

        var micros = (whole * 1_000_000) + long.Parse(match.Groups["fraction"].Value.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        return match.Groups["sign"].Value.Length > 0 ? -micros : micros;
    }

    /// <summary>Part of a whole as a percentage with one decimal, using integers only: <c>0.3%</c>.</summary>
    public static string Percent(long part, long whole)
    {
        if (whole <= 0)
        {
            return None;
        }

        var tenths = part * 1_000 / whole;
        return $"{tenths / 10}.{tenths % 10}%";
    }
}
