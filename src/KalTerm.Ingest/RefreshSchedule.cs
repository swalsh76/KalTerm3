namespace KalTerm.Ingest;

/// <summary>
/// When the market catalog is refreshed (owner's rule, 2026-10-18): every 5 minutes, except in the last 5 and the first 5 minutes of each
/// UTC hour, where it is every 30 seconds. Ticks are anchored to the clock (:00:00, :00:30 ... :04:30, :05:00, :10:00 ... :50:00, :55:00,
/// :55:30 ...), not to when the previous pass finished, so a pass that overruns skips the ticks it missed instead of piling them up.
/// </summary>
public static class RefreshSchedule
{
    /// <summary>Minutes at the start and end of the hour that get the fast cadence.</summary>
    public const int WindowMinutes = 5;

    /// <summary>The cadence inside the windows.</summary>
    public static TimeSpan FastInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The cadence outside the windows.</summary>
    public static TimeSpan SlowInterval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>True from :55:00 to :04:59.999 of each UTC hour.</summary>
    /// <param name="moment">The time to test.</param>
    /// <returns>Whether the fast cadence applies at that moment.</returns>
    public static bool InFastWindow(DateTimeOffset moment)
    {
        var minute = moment.UtcDateTime.Minute;
        return minute >= 60 - WindowMinutes || minute < WindowMinutes;
    }

    /// <summary>The next refresh time strictly after <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The next tick, in UTC.</returns>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var utc = new DateTimeOffset(now.UtcDateTime, TimeSpan.Zero);
        var fast = NextBoundary(utc, FastInterval);
        return InFastWindow(fast) ? fast : NextBoundary(utc, SlowInterval);
    }

    /// <summary>How long to wait from <paramref name="now"/> until the next refresh.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan DelayUntilNext(DateTimeOffset now) => NextRun(now) - now;

    private static DateTimeOffset NextBoundary(DateTimeOffset utc, TimeSpan interval)
    {
        var ticks = utc.Ticks / interval.Ticks * interval.Ticks;
        return new DateTimeOffset(ticks + interval.Ticks, TimeSpan.Zero);
    }
}
