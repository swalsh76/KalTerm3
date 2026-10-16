using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.Tests.Configuration;

public class KalshiEndpointsTests
{
    [Fact]
    public void ProductionUrlsAreExactlyKalshisPublishedOnes()
    {
        Assert.Equal("https://external-api.kalshi.com/trade-api/v2", KalshiEndpoints.Rest(KalshiEnvironment.Production).AbsoluteUri);
        Assert.Equal("wss://external-api-ws.kalshi.com/trade-api/ws/v2", KalshiEndpoints.WebSocket(KalshiEnvironment.Production).AbsoluteUri);
    }

    [Fact]
    public void DemoUrlsAreExactlyKalshisPublishedOnes()
    {
        Assert.Equal("https://external-api.demo.kalshi.co/trade-api/v2", KalshiEndpoints.Rest(KalshiEnvironment.Demo).AbsoluteUri);
        Assert.Equal("wss://external-api-ws.demo.kalshi.co/trade-api/ws/v2", KalshiEndpoints.WebSocket(KalshiEnvironment.Demo).AbsoluteUri);
    }

    [Theory]
    [InlineData(KalshiEnvironment.Demo)]
    [InlineData(KalshiEnvironment.Production)]
    public void RestIsSecureAndWebSocketIsSecure(KalshiEnvironment environment)
    {
        Assert.Equal("https", KalshiEndpoints.Rest(environment).Scheme);
        Assert.Equal("wss", KalshiEndpoints.WebSocket(environment).Scheme);
    }

    [Fact]
    public void DemoAndProductionNeverShareAHost()
    {
        Assert.NotEqual(KalshiEndpoints.Rest(KalshiEnvironment.Demo).Host, KalshiEndpoints.Rest(KalshiEnvironment.Production).Host);
        Assert.NotEqual(KalshiEndpoints.WebSocket(KalshiEnvironment.Demo).Host, KalshiEndpoints.WebSocket(KalshiEnvironment.Production).Host);
        Assert.Contains("demo", KalshiEndpoints.Rest(KalshiEnvironment.Demo).Host, StringComparison.Ordinal);
        Assert.DoesNotContain("demo", KalshiEndpoints.Rest(KalshiEnvironment.Production).Host, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEnvironmentIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KalshiEndpoints.Rest((KalshiEnvironment)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => KalshiEndpoints.WebSocket((KalshiEnvironment)42));
    }

    [Fact]
    public void TheEnvironmentEnumDefaultsToDemo()
    {
        Assert.Equal(KalshiEnvironment.Demo, default(KalshiEnvironment));   // an unset value can never mean production
    }
}
