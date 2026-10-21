using System.Net;
using System.Text;
using KalTerm.Core.Money;
using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>The reference's REST tests (kalshi-core/tests/test_rest.py, test_multivariate.py), ported, plus the extras.</summary>
public class KalshiRestClientTests
{
    private const string Base = "https://external-api.demo.kalshi.co/trade-api/v2";
    private const string StatusJson = """{"exchange_active":true,"trading_active":true}""";

    private static readonly int[] CappedBackoffMilliseconds = [500, 1000, 2000, 4000, 8000, 8000];

    private static string Market(string ticker) =>
        "{\"ticker\":\"" + ticker + "\",\"event_ticker\":\"EV-1\",\"market_type\":\"binary\",\"status\":\"active\"," +
        "\"yes_bid_dollars\":\"0.5600\",\"volume_fp\":\"10.00\",\"result\":\"\",\"unknown_future_field\":1}";

    private static string MarketsPage(string cursor, params string[] tickers) =>
        "{\"markets\":[" + string.Join(",", tickers.Select(Market)) + "],\"cursor\":\"" + cursor + "\"}";

    private static KalshiRestClient Client(
        StubHttpMessageHandler stub,
        AutoAdvanceTimeProvider time,
        KalshiSettings? settings = null,
        KalshiSigner? signer = null,
        TokenBucket? bucket = null,
        ILogger? logger = null,
        int maxRetries = 5,
        double jitter = 1.0) =>
        RestTestKit.Client(stub, time, settings, signer, bucket, logger, maxRetries, jitter);

    private static string SamplePem() => RestTestKit.SamplePem();

    [Fact]
    public async Task ExchangeStatusParses()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"exchange_active":true,"trading_active":false}""");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var status = await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ExchangeActive);
        Assert.False(status.TradingActive);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Base + "/exchange/status", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task RequestsAskForJsonAndIdentifyTheClient()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        var headers = stub.Requests[0].Headers;
        Assert.Equal("application/json", headers["Accept"]);
        Assert.StartsWith("KalTerm/", headers["User-Agent"], StringComparison.Ordinal);
        Assert.DoesNotContain("KALSHI-ACCESS-KEY", headers.Keys);   // no signer, no auth headers
    }

    [Fact]
    public async Task RequestsAreSignedOverTheFullPathWithoutTheQuery()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage(string.Empty));
        using var key = Ed25519PrivateKey.FromPem(SamplePem());
        using var client = Client(stub, time, signer: new KalshiSigner("kid", key, time));

        await client.MarketsPageAsync(KalshiQuery.Empty.With("limit", 5), TestContext.Current.CancellationToken);

        var headers = stub.Requests[0].Headers;
        Assert.Equal("kid", headers[KalshiSigner.KeyHeader]);
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.GetPublicKey(), KeyBlobFormat.RawPublicKey);
        var message = Encoding.UTF8.GetBytes(headers[KalshiSigner.TimestampHeader] + "GET/trade-api/v2/markets");
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, message, Convert.FromBase64String(headers[KalshiSigner.SignatureHeader])));
        Assert.EndsWith("/markets?limit=5", stub.Requests[0].Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachRetryIsSignedAfreshWithANewTimestamp()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable, "down").Respond(HttpStatusCode.OK, StatusJson);
        using var key = Ed25519PrivateKey.FromPem(SamplePem());
        using var client = Client(stub, time, signer: new KalshiSigner("kid", key, time));

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        var first = long.Parse(stub.Requests[0].Headers[KalshiSigner.TimestampHeader], CultureInfo.InvariantCulture);
        var second = long.Parse(stub.Requests[1].Headers[KalshiSigner.TimestampHeader], CultureInfo.InvariantCulture);
        Assert.True(second - first >= 500, $"the retry waited at least the 500 ms backoff, but timestamps differ by {second - first} ms");
        Assert.NotEqual(stub.Requests[0].Headers[KalshiSigner.SignatureHeader], stub.Requests[1].Headers[KalshiSigner.SignatureHeader]);
    }

    [Fact]
    public async Task MarketsPaginationFollowsTheCursor()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, MarketsPage("c1", "A", "B"))
            .Respond(HttpStatusCode.OK, MarketsPage(string.Empty, "C"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var tickers = new List<string>();
        await foreach (var market in client.IterateMarketsAsync(KalshiQuery.Empty.With("status", "open").With("limit", 2), TestContext.Current.CancellationToken))
        {
            tickers.Add(market.Ticker);
        }

        Assert.Equal(["A", "B", "C"], tickers);
        Assert.Equal(Base + "/markets?status=open&limit=2", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/markets?status=open&limit=2&cursor=c1", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task OpenMarketsAskForOpenNonComboMarketsAThousandAtATime()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, MarketsPage("c1", "A", "B"))
            .Respond(HttpStatusCode.OK, MarketsPage(string.Empty, "C"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var tickers = new List<string>();
        await foreach (var market in client.IterateOpenMarketsAsync(TestContext.Current.CancellationToken))
        {
            tickers.Add(market.Ticker);
        }

        Assert.Equal(["A", "B", "C"], tickers);
        Assert.Equal(Base + "/markets?status=open&mve_filter=exclude&limit=1000", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/markets?status=open&mve_filter=exclude&limit=1000&cursor=c1", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task UpdatedSinceAsksForChangedNonComboMarketsWithoutAStatusFilter()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, MarketsPage("c1", "A"))
            .Respond(HttpStatusCode.OK, MarketsPage(string.Empty, "B"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var tickers = new List<string>();
        await foreach (var market in client.IterateMarketsUpdatedSinceAsync(DateTimeOffset.FromUnixTimeSeconds(1_700_000_123), TestContext.Current.CancellationToken))
        {
            tickers.Add(market.Ticker);
        }

        Assert.Equal(["A", "B"], tickers);
        Assert.Equal(Base + "/markets?min_updated_ts=1700000123&mve_filter=exclude&limit=1000", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/markets?min_updated_ts=1700000123&mve_filter=exclude&limit=1000&cursor=c1", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task PaginationStopsWhenTheConsumerStops()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage("c1", "A", "B"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await foreach (var market in client.IterateMarketsAsync(null, TestContext.Current.CancellationToken))
        {
            Assert.Equal("A", market.Ticker);
            break;
        }

        Assert.Single(stub.Requests);   // the second page was never fetched
    }

    [Fact]
    public async Task MarketFieldsAreExact()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage(string.Empty, "A"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var page = await client.MarketsPageAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Dollars.Parse("0.56"), page.Markets[0].YesBidDollars);
        Assert.Equal(Contracts.Parse("10"), page.Markets[0].VolumeFp);
    }

    [Fact]
    public async Task TheMveFilterIsPassedThrough()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage(string.Empty, "A"));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.MarketsPageAsync(KalshiQuery.Empty.With("mve_filter", "only").With("status", "open"), TestContext.Current.CancellationToken);

        Assert.Contains("mve_filter=only", stub.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryValuesAreEscaped()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage(string.Empty));
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.MarketsPageAsync(KalshiQuery.Empty.With("tickers", "A B&C"), TestContext.Current.CancellationToken);

        Assert.EndsWith("?tickers=A%20B%26C", stub.Requests[0].Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retries429WithGrowingBackoffThenSucceeds()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, """{"error":"too many requests"}""")
            .Respond(HttpStatusCode.TooManyRequests, """{"error":"too many requests"}""")
            .Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, time, jitter: 1.0);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], time.Delays);   // 0.5 s x 2^attempt
        Assert.Equal(3, stub.Requests.Count);
    }

    [Theory]
    [InlineData(0.0, 250)]    // the smallest jitter factor is 0.5
    [InlineData(0.5, 375)]
    [InlineData(1.0, 500)]    // the largest is 1.0
    public async Task TheJitterScalesTheDelayBetweenHalfAndFull(double sample, int expectedMilliseconds)
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.BadGateway).Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, time, jitter: sample);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task TheBackoffIsCappedAtEightSeconds()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 6; i++)
        {
            stub.Respond(HttpStatusCode.ServiceUnavailable);
        }

        stub.Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, time, maxRetries: 6, jitter: 1.0);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CappedBackoffMilliseconds.Select(ms => TimeSpan.FromMilliseconds(ms)), time.Delays);   // 0.5 s x 2^5 = 16 s, capped at 8 s
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task EveryRetryableStatusIsRetried(HttpStatusCode status)
    {
        var stub = new StubHttpMessageHandler().Respond(status).Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task GivesUpAfterTheMaximumRetries()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable, "down").Respond(HttpStatusCode.ServiceUnavailable, "down")
            .Respond(HttpStatusCode.ServiceUnavailable, "down");
        using var client = Client(stub, new AutoAdvanceTimeProvider(), maxRetries: 2);

        var error = await Assert.ThrowsAsync<KalshiApiException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("down", error.Body);
        Assert.Equal(3, stub.Requests.Count);   // the first attempt plus two retries
    }

    [Fact]
    public async Task ZeroRetriesMeansOneAttempt()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable);
        using var client = Client(stub, new AutoAdvanceTimeProvider(), maxRetries: 0);

        await Assert.ThrowsAsync<KalshiApiException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ClientErrorsAreNotRetried(HttpStatusCode status)
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(status, """{"error":"bad"}""");
        using var client = Client(stub, time);

        var error = await Assert.ThrowsAsync<KalshiApiException>(() => client.MarketsPageAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Single(stub.Requests);
        Assert.Empty(time.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task OnlyStatus200CountsAsSuccess(HttpStatusCode status)
    {
        var stub = new StubHttpMessageHandler().Respond(status, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<KalshiApiException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task TheErrorMessageShortensALongBodyButKeepsItWhole()
    {
        var body = new string('x', 500);
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.BadRequest, body);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<KalshiApiException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Kalshi API error 400: " + new string('x', 200), error.Message);
        Assert.Equal(body, error.Body);
    }

    [Fact]
    public async Task TransportErrorsAreRetried()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("boom")).Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, time);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Single(time.Delays);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ATransportErrorOnTheLastAttemptIsRethrown()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("one")).Throw(new HttpRequestException("two"));
        using var client = Client(stub, new AutoAdvanceTimeProvider(), maxRetries: 1);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal("two", error.Message);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task AConnectionLostWhileReadingTheBodyIsRetried()
    {
        var stub = new StubHttpMessageHandler()
            .RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() })
            .Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var status = await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ExchangeActive);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ATimeoutIsRetriedLikeATransportError()
    {
        var stub = new StubHttpMessageHandler()
            .Throw(new TaskCanceledException("timed out", new TimeoutException()))
            .Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task TheCallersCancellationIsNeverRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var stub = new StubHttpMessageHandler().RespondWith(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("cancelled");
        });
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExchangeStatusAsync(cancellation.Token));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ACancelledCallerNeverSendsAnything()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var stub = new StubHttpMessageHandler();
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExchangeStatusAsync(cancellation.Token));

        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task NonGetRequestsAreBlockedBeforeAnythingIsSent(string method)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK);
        using var client = Client(stub, new AutoAdvanceTimeProvider());
        using var request = new HttpRequestMessage(new HttpMethod(method), Base + "/portfolio/orders");

        var error = await Assert.ThrowsAsync<ReadOnlyViolationException>(() => client.Http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal($"{method} /trade-api/v2/portfolio/orders blocked: client is read-only", error.Message);
        Assert.Empty(stub.Requests);   // blocked before anything is sent
    }

    [Fact]
    public void TheSynchronousSendPathIsGuardedToo()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK);
        using var client = Client(stub, new AutoAdvanceTimeProvider());
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/portfolio/orders");

        Assert.Throws<ReadOnlyViolationException>(() => client.Http.Send(request, TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }

    // Reads only. The account reads (balance, positions, orders, fills, settlements, queue positions; M6 slice 6.3) are GETs and need a signer. Placing, amending and cancelling orders stays the trading
    // client's job: nothing here sends anything but a GET.
    private static readonly string[] ApprovedPublicMethods =
    [
        "AccountLimitsAsync", "BalanceAsync", "ClockSkewAsync", "Dispose", "EndpointCostsAsync", "EventAsync", "EventWithMarketsAsync", "EventsPageAsync", "ExchangeStatusAsync", "FillsPageAsync", "IterateEventsAsync", "IterateFillsAsync",
        "IterateMarketsAsync", "IterateMarketsUpdatedSinceAsync", "IterateMultivariateEventsAsync", "IterateOpenMarketsAsync", "IterateOrdersAsync", "IteratePositionsAsync", "IterateSettlementsAsync",
        "IterateTradesAsync", "MarketAsync", "MarketsPageAsync", "MultivariateEventsPageAsync", "OrderAsync", "OrderBookAsync", "OrdersPageAsync", "PositionsPageAsync", "QueuePositionsAsync",
        "SeriesAsync", "SeriesListAsync", "SettlementsPageAsync",
    ];

    [Fact]
    public void TheClientsPublicSurfaceIsExactlyTheApprovedReadMethods()
    {
        // An allow-list, not a blocklist of write-sounding words: adding any public method, a write method above all,
        // fails here until someone deliberately extends this list. Order *books* are reads; placing orders is the trading client's job.
        var names = typeof(KalshiRestClient)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(ApprovedPublicMethods, names);
    }

    [Fact]
    public async Task EveryAttemptAcquiresRateLimitTokens()
    {
        // A bucket of 20 tokens that refills at 1 per second: two attempts (a failure and a success) spend all 20.
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.OK, StatusJson)
            .Respond(HttpStatusCode.OK, StatusJson);
        using var bucket = new TokenBucket(1, 20, time);
        using var client = Client(stub, time, bucket: bucket, jitter: 0.0);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);
        Assert.Single(time.Delays);   // just the 250 ms backoff

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        // The third request found the bucket nearly empty and had to wait for about 10 tokens at 1 per second.
        Assert.Contains(time.Delays, d => d >= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task ASingleAttemptSpendsOnlyOneRequestsTokens()
    {
        // The control for the test above: with no retry the same bucket still has tokens for the next call.
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, StatusJson).Respond(HttpStatusCode.OK, StatusJson);
        using var bucket = new TokenBucket(1, 20, time);
        using var client = Client(stub, time, bucket: bucket);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);
        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task ABucketPassedInStaysUsableAfterTheClientIsDisposed()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = new TokenBucket(100, 100, time);
        var client = Client(new StubHttpMessageHandler(), time, bucket: bucket);

        client.Dispose();

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);   // still the caller's
    }

    [Fact]
    public async Task DisposingTheClientDisposesItsHttpPipeline()
    {
        var client = Client(new StubHttpMessageHandler().Respond(HttpStatusCode.OK, StatusJson), new AutoAdvanceTimeProvider());

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABadBodyIsAResponseErrorNamingTheEndpoint()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"exchange_active":true}""");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<KalshiResponseException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("exchange status:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlErrorPageWithStatus200IsAResponseErrorNotACrash()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "<html>maintenance</html>");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<KalshiResponseException>(() => client.MarketsPageAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInvalidPageDuringPaginationIsAResponseError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, MarketsPage("c1", "A")).Respond(HttpStatusCode.OK, """{"markets":null}""");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var seen = new List<string>();
        await Assert.ThrowsAsync<KalshiResponseException>(async () =>
        {
            await foreach (var market in client.IterateMarketsAsync(null, TestContext.Current.CancellationToken))
            {
                seen.Add(market.Ticker);
            }
        });

        Assert.Equal(["A"], seen);   // the good page was delivered before the bad one failed
    }

    [Fact]
    public async Task ProductionLogsABannerAndUsesTheProductionHost()
    {
        var logger = new ListLogger();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, StatusJson);
        using var client = Client(stub, new AutoAdvanceTimeProvider(), KalshiSettings.Create(KalshiEnvironment.Production), logger: logger);

        await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

        var banner = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("PRODUCTION", banner.Message, StringComparison.Ordinal);
        Assert.Equal("external-api.kalshi.com", stub.Requests[0].Uri.Host);
    }

    [Fact]
    public void DemoLogsNoBanner()
    {
        var logger = new ListLogger();

        using var client = Client(new StubHttpMessageHandler(), new AutoAdvanceTimeProvider(), logger: logger);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("PRODUCTION", StringComparison.Ordinal));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task RetriesAreLoggedWithThePathAndNeverTheQueryOrHeaders()
    {
        var logger = new ListLogger();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.TooManyRequests).Respond(HttpStatusCode.OK, MarketsPage(string.Empty));
        using var client = Client(stub, new AutoAdvanceTimeProvider(), logger: logger);

        await client.MarketsPageAsync(KalshiQuery.Empty.With("tickers", "SECRET-LOOKING"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("/markets", entry.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 429", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-LOOKING", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => new KalshiRestClient(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KalshiRestClient(KalshiSettings.Default, new KalshiRestClientOptions { MaxRetries = -1 }));
    }

    [Fact]
    public async Task TheDefaultClientBuildsItsOwnReadBucketAndNeverTouchesTheNetworkInConstruction()
    {
        // No handler, bucket or clock supplied: construction alone sends nothing and disposal is clean.
        var client = new KalshiRestClient(KalshiSettings.Default);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExchangeStatusAsync(TestContext.Current.CancellationToken));
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("connection reset by peer");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
