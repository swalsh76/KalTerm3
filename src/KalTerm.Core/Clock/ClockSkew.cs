using System.Globalization;

namespace KalTerm.Core.Clock;

/// <summary>Whether the local clock is close enough to the server's for signed requests to work.</summary>
public enum ClockSkewStatus
{
    /// <summary>The whole interval is within the threshold.</summary>
    Ok,

    /// <summary>Even the nearest edge of the interval is beyond the threshold.</summary>
    Skewed,

    /// <summary>The interval straddles the threshold, so the clock is not guessed at.</summary>
    Uncertain,
}

/// <summary>
/// An estimate of the local clock's offset from the server's, as an interval. Offsets are <c>local - server</c>:
/// positive means the local clock is ahead.
/// </summary>
/// <param name="Offset">Best estimate of local minus server.</param>
/// <param name="Uncertainty">Half the width of the interval the true offset lies in.</param>
/// <param name="Low">Lower end of the interval.</param>
/// <param name="High">Upper end of the interval.</param>
/// <param name="Samples">How many samples were used.</param>
/// <param name="Consistent">False if the samples' intervals did not overlap, so the estimate has low confidence.</param>
/// <param name="Threshold">The largest skew that is acceptable.</param>
public sealed record ClockSkew(
    TimeSpan Offset,
    TimeSpan Uncertainty,
    TimeSpan Low,
    TimeSpan High,
    int Samples,
    bool Consistent,
    TimeSpan Threshold)
{
    public ClockSkewStatus Status
    {
        get
        {
            if (Low > Threshold || High < -Threshold)
            {
                return ClockSkewStatus.Skewed;   // even the closest edge of the interval is beyond the threshold
            }

            return -Threshold <= Low && High <= Threshold ? ClockSkewStatus.Ok : ClockSkewStatus.Uncertain;
        }
    }

    /// <summary>For example: <c>local clock is 3.00s ahead of Kalshi (±0.20s, 5 samples): skewed</c>.</summary>
    public string Describe()
    {
        var direction = Offset >= TimeSpan.Zero ? "ahead of" : "behind";
        var quality = Consistent ? string.Empty : " (samples disagreed; low confidence)";
        var status = Status switch
        {
            ClockSkewStatus.Ok => "ok",
            ClockSkewStatus.Skewed => "skewed",
            _ => "uncertain",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"local clock is {Seconds(Offset.Duration())}s {direction} Kalshi (±{Seconds(Uncertainty)}s, {Samples} samples){quality}: {status}");
    }

    /// <summary>Seconds with two decimals, rounded half to even on the exact tick value.</summary>
    private static string Seconds(TimeSpan value) =>
        decimal.Round(value.Ticks / (decimal)TimeSpan.TicksPerSecond, 2, MidpointRounding.ToEven)
            .ToString("F2", CultureInfo.InvariantCulture);
}
