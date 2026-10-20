using System.Globalization;

namespace KalTerm.Terminal.Services;

/// <summary>How values look to a person, in the time zone the person chose. The database and the API are UTC; the zone is only for showing. Same rules as the CLI.</summary>
/// <param name="zone">The zone times are shown in.</param>
internal sealed class Formatting(TimeZoneInfo zone)
{
    /// <summary>Shown where there is no value.</summary>
    public const string None = "-";

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

    /// <summary>An API time (UTC) shown in the zone as <c>2026-10-18 22:30:15</c>.</summary>
    public string Moment(string? iso)
    {
        if (iso is null)
        {
            return None;
        }

        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? TimeZoneInfo.ConvertTime(parsed, _zone).DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
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
    public static string Age(long? seconds) => seconds switch
    {
        null => None,
        < 120 => $"{seconds}s ago",
        < 7_200 => $"{seconds / 60}m ago",
        < 172_800 => $"{seconds / 3_600}h ago",
        _ => $"{seconds / 86_400}d ago",
    };

    /// <summary>A byte count as <c>264.0 MB</c>.</summary>
    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return None;
        }

        foreach (var (unit, step) in new (string, long)[] { ("GB", 1L << 30), ("MB", 1L << 20), ("kB", 1L << 10) })
        {
            if (value >= step)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{(double)value / step:0.0} {unit}");
            }
        }

        return $"{value} B";
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
