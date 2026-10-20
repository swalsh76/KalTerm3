using System.Net;
using System.Text.Json;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Tests.Keys;
using KalTerm.Trading.Tests.Risk;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Tests.Transport;

/// <summary>The scripted exchange every transport test talks to: nothing here ever reaches the network.</summary>
internal static class TransportKit
{
    public const string Coid = "11111111-2222-3333-4444-555555555555";
    public const string Orders = "/trade-api/v2/portfolio/events/orders";

    public const string Created = """{"order_id":"o-1","client_order_id":"11111111-2222-3333-4444-555555555555","fill_count":"0.00","remaining_count":"2.00","ts_ms":1}""";

    public const string Resting = """
        {"order_id":"o-1","client_order_id":"11111111-2222-3333-4444-555555555555","ticker":"KXA-1","status":"resting","action":"buy","outcome_side":"yes",
         "yes_price_dollars":"0.0100","no_price_dollars":"0.9900","initial_count_fp":"2.00","fill_count_fp":"0.00","remaining_count_fp":"2.00"}
        """;

    public static readonly OrderRequest Order = new("KXA-1", 3, BookSide.Bid, 200, 10_000, Coid);

    public static TradingSigner Signer(string environment = "demo")
    {
        var vault = new MemoryKeyVault();
        TradingKeyLoader.Store(vault, environment, "kid-test", TestKeys.NewPem());
        return TradingKeyLoader.Load(vault, environment)!;
    }

    public static string OrdersList(string body) => "{\"orders\":[" + body + "]}";

    public static JsonElement BodyOf(RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement.Clone();

    public static TradingFixture Client(StubHttpMessageHandler stub, Func<TimeProvider, TradingClientOptions>? options = null) => new(stub, options);
}

internal sealed class TradingFixture : IDisposable
{
    private readonly TradingSigner _signer = TransportKit.Signer();

    public TradingFixture(StubHttpMessageHandler stub, Func<TimeProvider, TradingClientOptions>? options)
    {
        Time = new AutoAdvanceTimeProvider();
        Stub = stub;
        Client = new KalshiTradingClient(KalshiSettings.Default, _signer, options?.Invoke(Time) ?? new TradingClientOptions { TimeProvider = Time }, stub);
    }

    public AutoAdvanceTimeProvider Time { get; }

    public StubHttpMessageHandler Stub { get; }

    public KalshiTradingClient Client { get; }

    public IReadOnlyList<double> Pauses => [.. Time.Delays.Select(d => d.TotalSeconds)];

    public void Dispose()
    {
        Client.Dispose();
        _signer.Dispose();
    }
}

public class TradingRoutesTests
{
    [Theory]
    [InlineData("POST", "/portfolio/events/orders")]
    [InlineData("DELETE", "/portfolio/events/orders")]
    [InlineData("DELETE", "/portfolio/events/orders/batched")]
    [InlineData("DELETE", "/portfolio/events/orders/o-1")]
    [InlineData("POST", "/portfolio/events/orders/o-1/amend")]
    [InlineData("POST", "/portfolio/events/orders/o-1/decrease")]
    [InlineData("POST", "/portfolio/intra_exchange_instance_transfer")]
    [InlineData("GET", "/portfolio/orders")]
    public void TheListedRoutesAreAllowed(string method, string path)
    {
        Assert.True(TradingRoutes.IsAllowed(new HttpMethod(method), path));
    }

    [Theory]
    [InlineData("GET", "/markets")]
    [InlineData("GET", "/portfolio/balance")]
    [InlineData("POST", "/portfolio/orders")]
    [InlineData("DELETE", "/portfolio/orders/o-1")]
    [InlineData("POST", "/portfolio/events/orders/o-1/anything")]
    [InlineData("POST", "/portfolio/events/orders/o-1/amend/extra")]
    [InlineData("PUT", "/portfolio/events/orders")]
    [InlineData("GET", "/portfolio/events/orders")]
    [InlineData("POST", "/portfolio/subaccounts/transfer")]
    [InlineData("POST", "/account/api_usage_level")]
    [InlineData("DELETE", "/portfolio/events/orders/a/b")]
    [InlineData("POST", "/portfolio/intra_exchange_instance_transfer/x")]
    public void EverythingElseIsRefused(string method, string path)
    {
        Assert.False(TradingRoutes.IsAllowed(new HttpMethod(method), path));
    }

    [Fact]
    public async Task ARequestOutsideTheListNeverReachesTheTransport()
    {
        var stub = new StubHttpMessageHandler();
        using var guarded = new HttpClient(new RouteGuardHandler(stub, "/trade-api/v2"));

        await Assert.ThrowsAsync<TradingRefusedException>(() => guarded.GetAsync(new Uri("https://example.invalid/trade-api/v2/portfolio/balance"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TradingRefusedException>(() => guarded.PostAsync(new Uri("https://example.invalid/trade-api/v2/portfolio/withdrawals"), new StringContent("{}"), TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }
}

public class TradingClientConstructionTests
{
    [Fact]
    public void ProductionIsRefusedUnlessAllowed()
    {
        using var signer = TransportKit.Signer("production");
        var production = KalshiSettings.Create(KalshiEnvironment.Production);

        Assert.Throws<TradingRefusedException>(() => new KalshiTradingClient(production, signer));
        using var allowed = new KalshiTradingClient(production, signer, new TradingClientOptions { AllowProduction = true }, new StubHttpMessageHandler());
        Assert.NotNull(allowed);
    }

    [Fact]
    public void AKeyForTheOtherEnvironmentIsRefused()
    {
        using var demoKey = TransportKit.Signer("demo");

        var thrown = Assert.Throws<TradingRefusedException>(() => new KalshiTradingClient(KalshiSettings.Create(KalshiEnvironment.Production), demoKey, new TradingClientOptions { AllowProduction = true }));

        Assert.Contains("demo", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoClientWithoutASigner()
    {
        Assert.Throws<ArgumentNullException>(() => new KalshiTradingClient(KalshiSettings.Default, null!));
    }
}

public class OrderPayloadTests
{
    [Fact]
    public async Task AnOrderIsSentOnceWithExactFixedPointAndASignedRequest()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.Created, TransportKit.Created);
        using var fixture = TransportKit.Client(stub);

        var created = await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(("o-1", 200L, false), (created.OrderId, created.RemainingCountE2, created.Recovered));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TransportKit.Orders, request.Uri.AbsolutePath);
        Assert.Contains("KALSHI-ACCESS-SIGNATURE", request.Headers.Keys);
        var body = TransportKit.BodyOf(request);
        Assert.Equal("KXA-1", body.GetProperty("ticker").GetString());
        Assert.Equal("bid", body.GetProperty("side").GetString());
        Assert.Equal("2.00", body.GetProperty("count").GetString());
        Assert.Equal("0.0100", body.GetProperty("price").GetString());
        Assert.Equal("good_till_canceled", body.GetProperty("time_in_force").GetString());
        Assert.Equal("taker_at_cross", body.GetProperty("self_trade_prevention_type").GetString());
        Assert.Equal(3, body.GetProperty("exchange_index").GetInt32());
        Assert.Equal(TransportKit.Coid, body.GetProperty("client_order_id").GetString());
        Assert.False(body.TryGetProperty("post_only", out _));
        Assert.False(body.TryGetProperty("reduce_only", out _));
        Assert.False(body.TryGetProperty("expiration_time", out _));
    }

    [Theory]
    [InlineData(1, 1_000_000 / 100, "0.01", "0.0100")]
    [InlineData(150, 999_900, "1.50", "0.9999")]
    [InlineData(12_345, 500_000, "123.45", "0.5000")]
    public void CountsAndPricesAreFormattedFromIntegersExactly(long countE2, long priceE6, string count, string price)
    {
        Assert.Equal(count, FixedPointText.Count(countE2));
        Assert.Equal(price, FixedPointText.Price(priceE6));
    }

    [Fact]
    public void APriceFinerThanFourDecimalsIsRefusedNotRounded()
    {
        Assert.Throws<ArgumentException>(() => FixedPointText.Price(10_050));
    }

    [Fact]
    public async Task OptionalFlagsAreSentOnlyWhenSet()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.Created, TransportKit.Created).Respond(HttpStatusCode.Created, TransportKit.Created);
        using var fixture = TransportKit.Client(stub);

        await fixture.Client.CreateOrderAsync(TransportKit.Order with { PostOnly = true, Expiration = new DateTimeOffset(2026, 10, 21, 12, 30, 0, TimeSpan.Zero), SelfTradePrevention = SelfTradePrevention.Maker }, TestContext.Current.CancellationToken);
        await fixture.Client.CreateOrderAsync(TransportKit.Order with { TimeInForce = TimeInForce.ImmediateOrCancel, ReduceOnly = true }, TestContext.Current.CancellationToken);

        var first = TransportKit.BodyOf(stub.Requests[0]);
        Assert.True(first.GetProperty("post_only").GetBoolean());
        Assert.Equal("2026-10-21T12:30:00Z", first.GetProperty("expiration_time").GetString());
        Assert.Equal("maker", first.GetProperty("self_trade_prevention_type").GetString());
        var second = TransportKit.BodyOf(stub.Requests[1]);
        Assert.True(second.GetProperty("reduce_only").GetBoolean());
        Assert.Equal("immediate_or_cancel", second.GetProperty("time_in_force").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AnOrderWithoutAClientOrderIdIsRefusedBeforeTheNetwork(string id)
    {
        var stub = new StubHttpMessageHandler();
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order with { ClientOrderId = id }, TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task MalformedOrdersAreRefusedBeforeTheNetwork()
    {
        var stub = new StubHttpMessageHandler();
        using var fixture = TransportKit.Client(stub);
        OrderRequest[] bad =
        [
            TransportKit.Order with { CountE2 = 0 },
            TransportKit.Order with { PriceE6 = 0 },
            TransportKit.Order with { PriceE6 = 1_000_000 },
            TransportKit.Order with { PriceE6 = 10_050 },
            TransportKit.Order with { ReduceOnly = true },
            TransportKit.Order with { TimeInForce = TimeInForce.FillOrKill, Expiration = DateTimeOffset.UnixEpoch },
            TransportKit.Order with { Ticker = " " },
        ];

        foreach (var order in bad)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.CreateOrderAsync(order, TestContext.Current.CancellationToken));
        }

        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(TradeAction.Buy, TradeOutcome.Yes, BookSide.Bid, 250_000)]
    [InlineData(TradeAction.Sell, TradeOutcome.Yes, BookSide.Ask, 250_000)]
    [InlineData(TradeAction.Buy, TradeOutcome.No, BookSide.Ask, 750_000)]
    [InlineData(TradeAction.Sell, TradeOutcome.No, BookSide.Bid, 750_000)]
    public void AnApprovedIntentBecomesAnOrderOnTheYesBook(TradeAction action, TradeOutcome outcome, BookSide side, long yesPrice)
    {
        var approved = RiskTestKit.Verdict(RiskTestKit.Order(action: action, outcome: outcome, count: 100, price: 250_000)).Approved!;

        var order = OrderRequest.From(approved, TransportKit.Coid);

        Assert.Equal((side, yesPrice, 100L, 0), (order.Side, order.PriceE6, order.CountE2, order.ExchangeIndex));
    }

    [Fact]
    public void ClientOrderIdsAreUniqueUuids()
    {
        var first = OrderRequest.NewClientOrderId();
        var second = OrderRequest.NewClientOrderId();

        Assert.NotEqual(first, second);
        Assert.Equal(first, Guid.Parse(first).ToString("D"));
    }
}

public class AmbiguousCreateTests
{
    [Fact]
    public async Task ALostAnswerIsResolvedByLookingTheOrderUpAndNeverResent()
    {
        var stub = new StubHttpMessageHandler()
            .Throw(new HttpRequestException("reset"))
            .Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        var created = await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

        Assert.True(created.Recovered);
        Assert.Equal(("o-1", 200L), (created.OrderId, created.RemainingCountE2));
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], stub.Requests.Select(r => r.Method));
        var query = stub.Requests[1].Uri.Query;
        Assert.Contains("ticker=KXA-1", query, StringComparison.Ordinal);
        Assert.Contains("limit=1000", query, StringComparison.Ordinal);
        Assert.Contains("min_ts=", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task AServerErrorIsAlsoResolvedByLookup(int status)
    {
        var stub = new StubHttpMessageHandler()
            .Respond((HttpStatusCode)status)
            .Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        var created = await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

        Assert.True(created.Recovered);
        Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AnOrderThatIsNeverFoundIsAnUnknownOutcomeAfterTheLastPause()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("reset"));
        for (var i = 0; i < 4; i++)
        {
            stub.Respond(HttpStatusCode.OK, TransportKit.OrdersList(string.Empty));
        }

        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken));

        Assert.Equal(("create_order", TransportKit.Coid), (thrown.Kind, thrown.Reference));
        Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal([0.5, 1.0, 2.0], fixture.Pauses);
    }

    [Fact]
    public async Task ALookupThatFailsEveryTimeIsAlsoAnUnknownOutcome()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("reset"));
        for (var i = 0; i < 16; i++)
        {
            stub.Throw(new HttpRequestException("down"));
        }

        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken));
        Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheLookupFollowsCursorsUntilItFindsTheOrder()
    {
        var other = TransportKit.Resting.Replace(TransportKit.Coid, "other", StringComparison.Ordinal).Replace("o-1", "o-2", StringComparison.Ordinal);
        var stub = new StubHttpMessageHandler()
            .Throw(new HttpRequestException("reset"))
            .Respond(HttpStatusCode.OK, "{\"orders\":[" + other + "],\"cursor\":\"page2\"}")
            .Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        var created = await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

        Assert.Equal("o-1", created.OrderId);
        Assert.Contains("cursor=page2", stub.Requests[2].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConflictIsResolvedByLookupAndIfTheOrderIsNotThereTheErrorStands()
    {
        var found = new StubHttpMessageHandler().Respond(HttpStatusCode.Conflict, "{}").Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using (var fixture = TransportKit.Client(found))
        {
            Assert.True((await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken)).Recovered);
        }

        var absent = new StubHttpMessageHandler().Respond(HttpStatusCode.Conflict, "{}");
        for (var i = 0; i < 4; i++)
        {
            absent.Respond(HttpStatusCode.OK, TransportKit.OrdersList(string.Empty));
        }

        using var second = TransportKit.Client(absent);
        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => second.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken));
        Assert.Equal(409, thrown.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task ADefiniteRefusalIsFinalAndNeverLookedUp(int status)
    {
        var stub = new StubHttpMessageHandler().Respond((HttpStatusCode)status, "{\"error\":\"no\"}");
        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken));

        Assert.Equal(status, thrown.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ARateLimitIsWaitedOutWithGrowingPausesAndThenTheOrderGoesThrough()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.Created, TransportKit.Created);
        using var fixture = TransportKit.Client(stub);

        var created = await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

        Assert.False(created.Recovered);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal([0.5, 1.0], fixture.Pauses);
    }

    [Fact]
    public async Task ARateLimitThatNeverEndsGivesUpAfterThreeRetriesAsAnApiError()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            stub.Respond(HttpStatusCode.TooManyRequests);
        }

        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken));

        Assert.Equal(429, thrown.StatusCode);
        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal([0.5, 1.0, 2.0], fixture.Pauses);
    }

    [Fact]
    public async Task ATimeoutCountsAsNoAnswer()
    {
        var stub = new StubHttpMessageHandler()
            .Throw(new TaskCanceledException("slow"))
            .Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        Assert.True((await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken)).Recovered);
    }

    [Fact]
    public async Task TheCallersOwnCancellationIsNotAnAmbiguousAnswer()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.Created, TransportKit.Created);
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.CreateOrderAsync(TransportKit.Order, cancelled.Token));
        Assert.Empty(stub.Requests);
    }
}

public class LookupTests
{
    [Fact]
    public async Task ALookupThatLosesItsConnectionIsRetriedWithGrowingPauses()
    {
        var stub = new StubHttpMessageHandler()
            .Throw(new HttpRequestException("down"))
            .Throw(new HttpRequestException("down"))
            .Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        var found = await fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.Equal("o-1", found!.OrderId);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal([0.5, 1.0], fixture.Pauses);
    }

    [Fact]
    public async Task ALookupGivesUpAfterTheLastPauseAndRaisesTheNetworkError()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            stub.Throw(new HttpRequestException("down"));
        }

        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal([0.5, 1.0, 2.0], fixture.Pauses);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ALookupAnsweredWithAServerErrorIsRetried(int status)
    {
        var stub = new StubHttpMessageHandler().Respond((HttpStatusCode)status).Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
        using var fixture = TransportKit.Client(stub);

        Assert.NotNull(await fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));
        Assert.Equal([0.5], fixture.Pauses);
    }

    [Fact]
    public async Task ALookupThatKeepsGettingAServerErrorRaisesTheApiError()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            stub.Respond(HttpStatusCode.ServiceUnavailable, "busy");
        }

        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Equal(503, thrown.StatusCode);
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task AClientErrorOnALookupIsFinalAndNeverRetried()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Equal(400, thrown.StatusCode);
        Assert.Single(stub.Requests);
        Assert.Empty(fixture.Pauses);
    }

    [Fact]
    public async Task AnEmptyListMeansNotFound()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"orders\":[],\"cursor\":\"\"}");
        using var fixture = TransportKit.Client(stub);

        Assert.Null(await fixture.Client.FindOrderAsync(TransportKit.Coid, "KXA-1", DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken));
    }
}

public class CancelTests
{
    private static readonly CancelTarget Target = new("o-1", 3, "KXA-1");

    [Fact]
    public async Task ACancelNamesTheShardAndTheMarket()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"order_id":"o-1","reduced_by":"2.00","ts_ms":1}""");
        using var fixture = TransportKit.Client(stub);

        var result = await fixture.Client.CancelOrderAsync(Target, TestContext.Current.CancellationToken);

        Assert.Equal((200L, false), (result.ReducedByE2, result.AlreadyGone));
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(TransportKit.Orders + "/o-1", request.Uri.AbsolutePath);
        Assert.Contains("exchange_index=3", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("market_ticker=KXA-1", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelAfterALostAnswerIsRetriedAndA404ThenMeansDone()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("slow")).Respond(HttpStatusCode.NotFound, "{}");
        using var fixture = TransportKit.Client(stub);

        var result = await fixture.Client.CancelOrderAsync(Target, TestContext.Current.CancellationToken);

        Assert.True(result.AlreadyGone);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task AFirstTry404IsAnErrorBecauseThereWasNoSuchOrder()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.NotFound, "{}");
        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.CancelOrderAsync(Target, TestContext.Current.CancellationToken));

        Assert.Equal(404, thrown.StatusCode);
    }

    [Fact]
    public async Task ACancelAfterAServerErrorThenSuccessIsNotAlreadyGone()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, """{"order_id":"o-1"}""");
        using var fixture = TransportKit.Client(stub);

        Assert.False((await fixture.Client.CancelOrderAsync(Target, TestContext.Current.CancellationToken)).AlreadyGone);
    }

    [Fact]
    public async Task ACancelThatKeepsFailingGivesUpAsUnknownAfterFourTries()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            stub.Throw(new HttpRequestException("slow"));
        }

        using var fixture = TransportKit.Client(stub);

        var thrown = await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.CancelOrderAsync(Target, TestContext.Current.CancellationToken));

        Assert.Equal(("cancel_order", "o-1"), (thrown.Kind, thrown.Reference));
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task ABatchCancelReportsEachOrderAndCancelAllSendsNothingElse()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"orders":[{"order_id":"o-1","reduced_by":"2.00"},{"order_id":"o-2","error":{"code":"not_found"}}]}""")
            .Respond(HttpStatusCode.NoContent, string.Empty);
        using var fixture = TransportKit.Client(stub);

        var results = await fixture.Client.CancelOrdersAsync([Target, new CancelTarget("o-2", 0, "KXB-1")], TestContext.Current.CancellationToken);
        await fixture.Client.CancelAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await fixture.Client.CancelOrdersAsync([], TestContext.Current.CancellationToken));

        Assert.Equal([null, "not_found"], results.Select(r => r.Error));
        Assert.Equal(2, stub.Requests.Count);
        var batch = TransportKit.BodyOf(stub.Requests[0]).GetProperty("orders");
        Assert.Equal(2, batch.GetArrayLength());
        Assert.Equal(("o-2", 0, "KXB-1"), (batch[1].GetProperty("order_id").GetString(), batch[1].GetProperty("exchange_index").GetInt32(), batch[1].GetProperty("market_ticker").GetString()));
        Assert.Equal(TransportKit.Orders + "/batched", stub.Requests[0].Uri.AbsolutePath);
        Assert.Equal(TransportKit.Orders, stub.Requests[1].Uri.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(stub.Requests[1].Body));
    }

    [Fact]
    public async Task CancellingIsRepeatedWhenTheAnswerIsLostBecauseTwiceIsHarmless()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.OK, """{"orders":[{"order_id":"o-1","reduced_by":"2.00"}]}""")
            .Throw(new HttpRequestException("slow"))
            .Respond(HttpStatusCode.NoContent, string.Empty);
        using var fixture = TransportKit.Client(stub);

        var results = await fixture.Client.CancelOrdersAsync([Target], TestContext.Current.CancellationToken);
        await fixture.Client.CancelAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("o-1", Assert.Single(results).OrderId);
        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal([0.5, 0.5], fixture.Pauses);
    }

    [Fact]
    public async Task ACancelAllAndABatchThatKeepFailingAreUnknownOutcomes()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 8; i++)
        {
            stub.Respond(HttpStatusCode.BadGateway);
        }

        using var fixture = TransportKit.Client(stub);

        Assert.Equal("cancel_orders", (await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.CancelOrdersAsync([Target], TestContext.Current.CancellationToken))).Kind);
        Assert.Equal("cancel_all", (await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.CancelAllAsync(TestContext.Current.CancellationToken))).Kind);
    }

    [Fact]
    public async Task ARefusedCancelAllOrBatchIsAnApiError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.Forbidden).Respond(HttpStatusCode.Forbidden);
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.CancelOrdersAsync([Target], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.CancelAllAsync(TestContext.Current.CancellationToken));
    }
}

public class AmendDecreaseTransferTests
{
    private static readonly string[] AmendFields = ["ticker", "side", "price", "count"];

    [Fact]
    public async Task AmendAndDecreaseSendTheDocumentedBodies()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"order_id":"o-1","remaining_count":"3.00","fill_count":"0.00"}""")
            .Respond(HttpStatusCode.OK, """{"order_id":"o-1","remaining_count":"1.00"}""")
            .Respond(HttpStatusCode.OK, """{"order_id":"o-1"}""");
        using var fixture = TransportKit.Client(stub);

        var amended = await fixture.Client.AmendOrderAsync("o-1", "KXA-1", BookSide.Ask, 500_000, 300, 3, TestContext.Current.CancellationToken);
        var by = await fixture.Client.DecreaseOrderAsync("o-1", 3, "KXA-1", reduceByE2: 100, cancellationToken: TestContext.Current.CancellationToken);
        var to = await fixture.Client.DecreaseOrderAsync("o-1", 3, "KXA-1", reduceToE2: 100, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((300L, 100L, (long?)null), (amended.RemainingCountE2, by.RemainingCountE2, to.RemainingCountE2));
        Assert.Equal(TransportKit.Orders + "/o-1/amend", stub.Requests[0].Uri.AbsolutePath);
        var amend = TransportKit.BodyOf(stub.Requests[0]);
        Assert.Equal(["KXA-1", "ask", "0.5000", "3.00"], AmendFields.Select(n => amend.GetProperty(n).GetString()));
        Assert.Equal(3, amend.GetProperty("exchange_index").GetInt32());
        Assert.Equal(TransportKit.Orders + "/o-1/decrease", stub.Requests[1].Uri.AbsolutePath);
        Assert.Equal("1.00", TransportKit.BodyOf(stub.Requests[1]).GetProperty("reduce_by").GetString());
        Assert.False(TransportKit.BodyOf(stub.Requests[1]).TryGetProperty("reduce_to", out _));
        Assert.Equal("1.00", TransportKit.BodyOf(stub.Requests[2]).GetProperty("reduce_to").GetString());
        Assert.False(TransportKit.BodyOf(stub.Requests[2]).TryGetProperty("reduce_by", out _));
    }

    [Fact]
    public async Task ADecreaseNeedsExactlyOneTarget()
    {
        var stub = new StubHttpMessageHandler();
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.DecreaseOrderAsync("o-1", 0, "KXA-1", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.DecreaseOrderAsync("o-1", 0, "KXA-1", 100, 100, TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AmendAndDecreaseAreNeverResentAfterALostAnswer()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("slow")).Respond(HttpStatusCode.ServiceUnavailable);
        using var fixture = TransportKit.Client(stub);

        var amend = await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.AmendOrderAsync("o-1", "KXA-1", BookSide.Bid, 100_000, 100, 0, TestContext.Current.CancellationToken));
        var decrease = await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.DecreaseOrderAsync("o-1", 0, "KXA-1", reduceByE2: 100, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(("amend_order", "o-1"), (amend.Kind, amend.Reference));
        Assert.Equal("decrease_order", decrease.Kind);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ARefusedAmendIsAnApiError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.BadRequest, "{}").Respond(HttpStatusCode.BadRequest, "{}");
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.AmendOrderAsync("o-1", "KXA-1", BookSide.Bid, 100_000, 100, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.DecreaseOrderAsync("o-1", 0, "KXA-1", reduceToE2: 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmendValuesOutOfRangeAreRefusedBeforeTheNetwork()
    {
        var stub = new StubHttpMessageHandler();
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.AmendOrderAsync("o-1", "KXA-1", BookSide.Bid, 1_000_000, 100, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.AmendOrderAsync("o-1", "KXA-1", BookSide.Bid, 100_000, 0, 0, TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ATransferIsInCenticents()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"transfer_id":"t-9"}""");
        using var fixture = TransportKit.Client(stub);

        var id = await fixture.Client.TransferAsync(0, 3, 2_500_100, "tr-1", TestContext.Current.CancellationToken);

        Assert.Equal("t-9", id);
        var body = TransportKit.BodyOf(Assert.Single(stub.Requests));
        Assert.Equal(("event_contract", "event_contract", 25_001L, "tr-1", 0, 3), (body.GetProperty("source").GetString(), body.GetProperty("destination").GetString(), body.GetProperty("amount").GetInt64(), body.GetProperty("client_transfer_id").GetString(), body.GetProperty("source_exchange_shard").GetInt32(), body.GetProperty("destination_exchange_shard").GetInt32()));
    }

    [Fact]
    public async Task ATransferWithNoClearAnswerIsNeverSentAgain()
    {
        foreach (var failure in new Action<StubHttpMessageHandler>[] { s => s.Respond(HttpStatusCode.ServiceUnavailable), s => s.Respond(HttpStatusCode.InternalServerError), s => s.Throw(new HttpRequestException("x")), s => s.Throw(new TaskCanceledException("slow")) })
        {
            var stub = new StubHttpMessageHandler();
            failure(stub);
            stub.Respond(HttpStatusCode.OK, """{"transfer_id":"t-9"}""");
            using var fixture = TransportKit.Client(stub);

            var thrown = await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.TransferAsync(0, 3, 1_000_000, "tr-1", TestContext.Current.CancellationToken));

            Assert.Equal(("transfer", "tr-1"), (thrown.Kind, thrown.Reference));
            Assert.Single(stub.Requests);
        }
    }

    [Fact]
    public async Task ARefusedTransferIsAnApiErrorAndARateLimitIsWaitedOut()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.TooManyRequests).Respond(HttpStatusCode.OK, """{"transfer_id":"t-1"}""").Respond(HttpStatusCode.BadRequest, "{}");
        using var fixture = TransportKit.Client(stub);

        Assert.Equal("t-1", await fixture.Client.TransferAsync(0, 1, 100, "a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KalshiApiException>(() => fixture.Client.TransferAsync(0, 1, 100, "b", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    [InlineData(150L)]
    [InlineData(1L)]
    public async Task BadTransferAmountsAreRefusedBeforeTheNetwork(long amount)
    {
        var stub = new StubHttpMessageHandler();
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.TransferAsync(0, 1, amount, "t", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.TransferAsync(1, 1, 100, "t", TestContext.Current.CancellationToken));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ATransferReplyWithoutAnIdIsAResponseError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{}");
        using var fixture = TransportKit.Client(stub);

        await Assert.ThrowsAsync<KalTerm.Kalshi.KalshiResponseException>(() => fixture.Client.TransferAsync(0, 1, 100, "t", TestContext.Current.CancellationToken));
    }
}

public class WriteBudgetTests
{
    [Fact]
    public async Task WritesSpendTheDocumentedTokenCostsFromTheWriteBucket()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 10; i++)
        {
            stub.Respond(HttpStatusCode.Created, TransportKit.Created);
        }

        for (var i = 0; i < 5; i++)
        {
            stub.Respond(HttpStatusCode.OK, """{"order_id":"o-1"}""");
        }

        stub.Respond(HttpStatusCode.OK, """{"orders":[]}""");
        using var bucket = new KalTerm.Core.RateLimiting.TokenBucket(90, 90, time);
        using var fixture = new TradingFixture(stub, _ => new TradingClientOptions { TimeProvider = time, Bucket = bucket });

        for (var i = 0; i < 9; i++)
        {
            await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);   // 9 x 10 = 90: the whole bucket
        }

        Assert.Empty(time.Delays);
        await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);   // 10 more: waits 10 / 90 s
        Assert.Equal(10.0 / 90, time.Delays.Single().TotalSeconds, 2);
        var before = time.Delays.Count;
        for (var i = 0; i < 5; i++)
        {
            await fixture.Client.CancelOrderAsync(new CancelTarget("o-1", 0, "KXA-1"), TestContext.Current.CancellationToken);   // 2 tokens each
        }

        await fixture.Client.CancelOrdersAsync([new CancelTarget("o-1", 0, "KXA-1")], TestContext.Current.CancellationToken);   // 2 per order
        Assert.True(time.Delays.Count > before);
        Assert.Equal(((5 * 2) + 2) / 90.0, time.Delays.Skip(1).Sum(d => d.TotalSeconds), 2);
    }

    [Fact]
    public void TheWriteBucketFollowsTheAccountTier()
    {
        using var bucket = KalTerm.Kalshi.RateLimiting.KalshiRateLimits.WriteBucket(KalshiSettings.Create(writeTokensPerSecond: 300));
        Assert.Equal(270m, bucket.Capacity);
    }
}

public class AuditTests
{
    [Fact]
    public async Task EveryWriteAndItsAnswerIsRecordedWithoutAnySecret()
    {
        var path = Path.Combine(Path.GetTempPath(), "kalterm-audit-" + Guid.NewGuid().ToString("N"), "audit.jsonl");
        try
        {
            var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("reset")).Respond(HttpStatusCode.OK, TransportKit.OrdersList(TransportKit.Resting));
            using var fixture = TransportKit.Client(stub, t => new TradingClientOptions { TimeProvider = t, Audit = new JsonLinesAudit(path) });

            await fixture.Client.CreateOrderAsync(TransportKit.Order, TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Contains(lines, l => l.GetProperty("what").GetString() == "POST /portfolio/events/orders");
            Assert.Contains(lines, l => l.GetProperty("what").GetString() == "reply POST /portfolio/events/orders" && l.GetProperty("status").GetInt32() == 0);
            Assert.Contains(lines, l => l.GetProperty("what").GetString() == "create_order.recovered");
            Assert.All(lines, l => Assert.Equal(TransportKit.Coid, l.GetProperty("ref").GetString()));
            Assert.DoesNotContain("SIGNATURE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kid-test", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ABlankAuditPathIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new JsonLinesAudit(" "));
    }

    [Fact]
    public async Task AnUnknownOutcomeIsRecorded()
    {
        var recorded = new List<string>();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable);
        using var fixture = TransportKit.Client(stub, t => new TradingClientOptions { TimeProvider = t, Audit = new ListAudit(recorded) });

        await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Client.TransferAsync(0, 1, 100, "tr-9", TestContext.Current.CancellationToken));

        Assert.Contains("transfer.unknown tr-9 503", recorded);
    }

    private sealed class ListAudit(List<string> into) : ITradingAudit
    {
        public void Record(DateTimeOffset at, string what, string reference, int? status, string? detail) => into.Add($"{what} {reference} {status}".TrimEnd());
    }
}
