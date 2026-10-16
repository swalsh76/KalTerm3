namespace KalTerm.Kalshi.Configuration;

/// <summary>
/// The only place Kalshi URLs are defined. Nothing else in the code base may spell out a Kalshi host name
/// (a test scans the sources and fails if one does), so changing or auditing an endpoint is a one-file job.
/// </summary>
public static class KalshiEndpoints
{
    private static readonly Uri DemoRest = new("https://external-api.demo.kalshi.co/trade-api/v2", UriKind.Absolute);
    private static readonly Uri ProductionRest = new("https://external-api.kalshi.com/trade-api/v2", UriKind.Absolute);
    private static readonly Uri DemoWebSocket = new("wss://external-api-ws.demo.kalshi.co/trade-api/ws/v2", UriKind.Absolute);
    private static readonly Uri ProductionWebSocket = new("wss://external-api-ws.kalshi.com/trade-api/ws/v2", UriKind.Absolute);

    /// <summary>The REST API base URL, including its <c>/trade-api/v2</c> path.</summary>
    public static Uri Rest(KalshiEnvironment environment) => environment switch
    {
        KalshiEnvironment.Demo => DemoRest,
        KalshiEnvironment.Production => ProductionRest,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "unknown Kalshi environment"),
    };

    /// <summary>The WebSocket API URL.</summary>
    public static Uri WebSocket(KalshiEnvironment environment) => environment switch
    {
        KalshiEnvironment.Demo => DemoWebSocket,
        KalshiEnvironment.Production => ProductionWebSocket,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "unknown Kalshi environment"),
    };
}
