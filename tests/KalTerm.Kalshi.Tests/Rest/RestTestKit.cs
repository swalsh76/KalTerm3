using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using Microsoft.Extensions.Logging;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>What the REST client tests share: a client wired to a scripted handler and a fake clock.</summary>
internal static class RestTestKit
{
    internal const string Base = "https://external-api.demo.kalshi.co/trade-api/v2";
    internal const string StatusJson = """{"exchange_active":true,"trading_active":true}""";

    internal static KalshiRestClient Client(
        StubHttpMessageHandler stub,
        AutoAdvanceTimeProvider time,
        KalshiSettings? settings = null,
        KalshiSigner? signer = null,
        TokenBucket? bucket = null,
        ILogger? logger = null,
        int maxRetries = 5,
        double jitter = 1.0) =>
        new(
            settings ?? KalshiSettings.Default,
            new KalshiRestClientOptions
            {
                Signer = signer,
                Bucket = bucket,
                TimeProvider = time,
                Logger = logger,
                MaxRetries = maxRetries,
                JitterSample = () => jitter,
            },
            stub);

    internal static string SamplePem() =>
        VectorFile.Load("ed25519.json").GetProperty("rawSign")[3].GetProperty("pkcs8Pem").GetString()!;

    internal static string Market(string ticker) =>
        "{\"ticker\":\"" + ticker + "\",\"event_ticker\":\"EV-1\",\"market_type\":\"binary\",\"status\":\"active\"," +
        "\"yes_bid_dollars\":\"0.5600\",\"volume_fp\":\"10.00\",\"result\":\"\",\"unknown_future_field\":1}";
}
