using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace KalTerm.Api.Tests;

public sealed class KestrelLimitsTests
{
    [Fact]
    public void TheLimitsAreSetAndTheServerDoesNotNameItself()
    {
        var options = new KestrelServerOptions();

        KestrelLimits.Apply(options);

        Assert.False(options.AddServerHeader);
        Assert.Equal(KestrelLimits.MaxRequestBodyBytes, options.Limits.MaxRequestBodySize);
        Assert.Equal(KestrelLimits.MaxConnections, options.Limits.MaxConcurrentConnections);
        Assert.Equal(KestrelLimits.HeadersTimeout, options.Limits.RequestHeadersTimeout);
        Assert.NotNull(options.Limits.MinRequestBodyDataRate);
    }

    [Fact]
    public void TheBodyLimitLeavesRoomForTheBiggestLayout()
    {
        Assert.True(KestrelLimits.MaxRequestBodyBytes >= 2 * 32 * 1024);
        Assert.Throws<ArgumentNullException>(() => KestrelLimits.Apply(null!));
    }
}
