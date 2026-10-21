using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Client;
using Spectre.Console;

namespace KalTerm.Cli;

/// <summary>
/// How values look to a person. The API sends exact decimal strings; these only shorten them for reading (<c>0.560000</c> becomes <c>0.56</c>) and never
/// change what they mean. <c>--json</c> prints the API's own text, so the display is never the record.
/// </summary>
internal static partial class Display
{
    /// <summary>Shown where there is no value.</summary>
    public const string None = ValueText.None;

    /// <summary>Makes text safe to put in Spectre markup (API text may contain brackets).</summary>
    public static string Escape(string? text) => Markup.Escape(text ?? string.Empty);

    /// <summary>Dollars without trailing zeros but with at least two decimals: <c>0.560000</c> gives <c>0.56</c>, <c>1.000000</c> gives <c>1.00</c>.</summary>
    public static string Dollars(string? value) => ValueText.Dollars(value);

    /// <summary>Contracts with thousands separators; a whole number loses its <c>.00</c>: <c>1234.00</c> gives <c>1,234</c>, <c>20.50</c> stays <c>20.50</c>.</summary>
    public static string Count(string? value) => ValueText.Count(value);

    private static readonly AsyncLocal<TimeZoneInfo?> CurrentZone = new();

    /// <summary>The time zone times are shown in for the command being run (UTC until a command chooses one; flows with the command, so parallel commands do not share it).</summary>
    public static TimeZoneInfo Zone
    {
        get => CurrentZone.Value ?? TimeZoneInfo.Utc;
        set => CurrentZone.Value = value;
    }

    /// <summary>The zone's name for a column header: <c>UTC</c> or the zone's id.</summary>
    public static string ZoneName => Zone.Equals(TimeZoneInfo.Utc) ? "UTC" : Zone.Id;

    /// <summary>An API time (sent in UTC) shown in <see cref="Zone"/> as <c>2026-10-18 22:30:15</c>, with milliseconds when asked.</summary>
    public static string Moment(string? iso, bool milliseconds = false)
    {
        if (iso is null)
        {
            return None;
        }

        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return iso;
        }

        return TimeZoneInfo.ConvertTime(parsed, Zone).DateTime.ToString(milliseconds ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Like <see cref="Moment"/>, followed by the zone: <c>UTC</c>, or the offset that applied at that moment, for example <c>-06:00</c>.</summary>
    public static string MomentWithZone(string? iso, bool milliseconds = false)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return Moment(iso, milliseconds);
        }

        var local = TimeZoneInfo.ConvertTime(parsed, Zone);
        var zone = Zone.Equals(TimeZoneInfo.Utc) ? "UTC" : local.ToString("zzz", CultureInfo.InvariantCulture);
        return $"{Moment(iso, milliseconds)} {zone}";
    }

    /// <summary>How long ago, coarsely: <c>45s ago</c>, <c>12m ago</c>, <c>5h ago</c>, <c>3d ago</c>.</summary>
    public static string Age(long? seconds) => ValueText.Age(seconds);

    /// <summary>A byte count as <c>264.0 MB</c>.</summary>
    public static string Size(long? bytes) => ValueText.Size(bytes);

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

    /// <summary>A length of time as <c>45s</c>, <c>7m 0s</c>, <c>2h 5m</c> or <c>3d 4h</c>.</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds switch
        {
            < 60 => $"{(int)span.TotalSeconds}s",
            < 3_600 => $"{(int)span.TotalMinutes}m {span.Seconds}s",
            < 86_400 => $"{(int)span.TotalHours}h {span.Minutes}m",
            _ => $"{(int)span.TotalDays}d {span.Hours}h",
        };
    }

    /// <summary>Parses <c>30s</c>, <c>15m</c>, <c>2h</c> or <c>7d</c>; anything else (including zero) is refused.</summary>
    public static bool TryParseSince(string? text, out TimeSpan span)
    {
        span = default;
        if (text is null || SinceText().Match(text) is not { Success: true } match || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0)
        {
            return false;
        }

        span = match.Groups["unit"].Value switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromDays(n),
        };
        return true;
    }

    [GeneratedRegex("^(?<n>[0-9]{1,6})(?<unit>[smhd])$", RegexOptions.CultureInvariant)]
    private static partial Regex SinceText();
}
