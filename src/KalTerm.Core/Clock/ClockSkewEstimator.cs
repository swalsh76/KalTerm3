namespace KalTerm.Core.Clock;

/// <summary>
/// Estimates the local clock's offset from the server's. Signed requests embed a timestamp, so a badly skewed clock
/// makes every request fail, yet Kalshi publishes no time endpoint and no tolerance. The only server time available
/// is the HTTP <c>Date</c> header, which has one-second resolution, so each sample yields an interval for the offset
/// and several spaced samples are intersected to narrow it. All arithmetic is exact (100 ns ticks).
/// </summary>
public static class ClockSkewEstimator
{
    /// <summary>Our own choice of limit; Kalshi's tolerance is undocumented.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <exception cref="ClockCheckException">There are no samples.</exception>
    /// <exception cref="ArgumentException">A sample was received before it was sent.</exception>
    public static ClockSkew Estimate(IReadOnlyList<ClockSample> samples, TimeSpan? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ClockCheckException("no samples");
        }

        var limit = threshold ?? DefaultThreshold;

        // The server stamped its clock at some local instant in [Sent, Received]; its true time is in
        // [ServerTime, ServerTime + 1 s) because Date is truncated to whole seconds.
        var intervals = new List<(TimeSpan Low, TimeSpan High)>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Received < sample.Sent)
            {
                throw new ArgumentException("a sample cannot be received before it was sent", nameof(samples));
            }

            intervals.Add((sample.Sent - sample.ServerTime - OneSecond, sample.Received - sample.ServerTime));
        }

        var low = intervals.Max(i => i.Low);
        var high = intervals.Min(i => i.High);
        if (low <= high)
        {
            return new ClockSkew(Midpoint(low, high), Half(high - low), low, high, samples.Count, Consistent: true, limit);
        }

        // Inconsistent (for example a cached response): fall back to the median, with the widest interval.
        var midpoints = intervals.Select(i => Midpoint(i.Low, i.High)).Order().ToList();
        var offset = midpoints.Count % 2 == 1
            ? midpoints[midpoints.Count / 2]
            : Midpoint(midpoints[(midpoints.Count / 2) - 1], midpoints[midpoints.Count / 2]);
        var widest = Half(intervals.Max(i => i.High - i.Low));
        return new ClockSkew(offset, widest, offset - widest, offset + widest, samples.Count, Consistent: false, limit);
    }

    private static TimeSpan Midpoint(TimeSpan a, TimeSpan b) => TimeSpan.FromTicks((a.Ticks + b.Ticks) / 2);

    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);
}
