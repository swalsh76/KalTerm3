using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace KalTerm.Api;

/// <summary>Limits on what one client can ask of the API: a small body, a short wait for headers and a body that arrives at a minimum rate. The biggest body the API accepts is a layout of 32 KB.</summary>
public static class KestrelLimits
{
    /// <summary>The largest request body in bytes.</summary>
    public const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>The most connections open at once.</summary>
    public const long MaxConnections = 200;

    /// <summary>How long a client may take to send its headers.</summary>
    public static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Sets the limits and stops the server naming itself in a header.</summary>
    /// <param name="options">The server options.</param>
    public static void Apply(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        options.Limits.MaxConcurrentConnections = MaxConnections;
        options.Limits.RequestHeadersTimeout = HeadersTimeout;
        options.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(10));
    }
}
