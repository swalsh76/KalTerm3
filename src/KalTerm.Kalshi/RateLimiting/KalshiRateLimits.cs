using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.RateLimiting;

/// <summary>
/// Builds the client-side token buckets from the settings. Kalshi budgets tokens per second (Basic tier: 200 read,
/// 100 write); read and write are independent buckets that hold at most one second of budget, scaled by the margin.
/// The caller owns, and should dispose, the bucket.
/// </summary>
public static class KalshiRateLimits
{
    /// <summary>Bucket for read requests: the tier budget times the safety margin.</summary>
    public static TokenBucket ReadBucket(KalshiSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TokenBucket(settings.ReadBudget, settings.ReadBudget, timeProvider);
    }

    /// <summary>Bucket for order writes: the tier budget times the safety margin.</summary>
    public static TokenBucket WriteBucket(KalshiSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TokenBucket(settings.WriteBudget, settings.WriteBudget, timeProvider);
    }
}
