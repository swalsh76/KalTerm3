using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Auth;
using Microsoft.Extensions.Logging;

namespace KalTerm.Kalshi.Rest;

/// <summary>Optional settings for <see cref="KalshiRestClient"/>. Every property has a sensible default.</summary>
public sealed class KalshiRestClientOptions
{
    /// <summary>Signs each request. Public market data needs none; account endpoints would.</summary>
    public KalshiSigner? Signer { get; init; }

    /// <summary>
    /// The rate-limit bucket. When omitted the client builds the read bucket from the settings and disposes it; a bucket
    /// passed in stays the caller's to dispose.
    /// </summary>
    public TokenBucket? Bucket { get; init; }

    /// <summary>The clock for backoff waits and signing timestamps; <see cref="TimeProvider.System"/> when omitted.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the production banner and retry notices; nothing is logged when omitted.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Retries after the first attempt (so the most requests sent is this plus one). Default 5.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Per-attempt HTTP timeout; a timeout counts as a transport error and is retried. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>For tests: a sample in [0, 1) replacing the random jitter source.</summary>
    internal Func<double>? JitterSample { get; init; }
}
