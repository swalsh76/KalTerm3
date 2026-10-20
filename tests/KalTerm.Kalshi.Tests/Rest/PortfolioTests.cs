using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>
/// Account reads. The payloads carry exactly the field sets measured on a live account (the Python reference's fixtures, BUILD-LOG 8.1; the values are invented), and a test fails if a payload has a field the
/// model lacks. The account endpoints need a signer; with none, nothing is sent.
/// </summary>
public class PortfolioTests
{
    private const string Base = RestTestKit.Base;

    internal const string BalanceJson = """
        {"balance":1034,"balance_dollars":"10.3490","portfolio_value":250,"updated_ts":1791908624,
         "balance_breakdown":[{"balance":"10.3490","exchange_index":0},{"balance":"0.0000","exchange_index":1},{"balance":"2.5000","exchange_index":3}]}
        """;

    internal const string PositionJson = """
        {"ticker":"KXA-1","exchange_index":0,"total_traded_dollars":"1.250000","position_fp":"-3.00","market_exposure_dollars":"1.500000",
         "realized_pnl_dollars":"-0.100000","fees_paid_dollars":"0.020000","last_updated_ts":"2026-10-13T12:00:00Z"}
        """;

    internal const string EventPositionJson = """
        {"event_ticker":"KXA","total_cost_dollars":"1.25","total_cost_shares_fp":"3.00","event_exposure_dollars":"1.5","realized_pnl_dollars":"0","fees_paid_dollars":"0.02"}
        """;

    internal const string OrderJson = """
        {"action":"sell","book_side":"ask","client_order_id":"e2300918-c088-4100-992a-6eee2b09dd29","created_time":"2026-10-02T05:07:05.899187Z","exchange_index":0,
         "fill_count_fp":"1.00","initial_count_fp":"2.00","last_update_time":"2026-10-02T05:07:05.899187Z","maker_fees_dollars":"0.000000","maker_fill_cost_dollars":"0.000000",
         "no_price_dollars":"0.9900","order_id":"01a0fb02-44a8-776b-9801-c464cfe31239","outcome_side":"no","remaining_count_fp":"1.00","self_trade_prevention_type":"taker_at_cross",
         "side":"yes","status":"resting","subaccount_number":0,"taker_fees_dollars":"0.000700","taker_fill_cost_dollars":"0.990000","ticker":"KXA-1","type":"limit","user_id":"u-1",
         "yes_price_dollars":"0.0100"}
        """;

    internal const string FillJson = """
        {"action":"sell","book_side":"ask","count_fp":"1.00","created_time":"2026-10-02T05:07:05.899187Z","exchange_index":0,"fee_cost":"0.000700",
         "fill_id":"07231bca-c049-a753-f68e-059a8e3283ad","is_taker":true,"market_ticker":"KXA-1","no_price_dollars":"0.9900","order_id":"01a0fb02-44a8-776b-9801-c464cfe31239",
         "outcome_side":"no","side":"no","subaccount_number":0,"ticker":"KXA-1","trade_id":"07231bca-c049-a753-f68e-059a8e3283ad","ts":1790917625,"yes_price_dollars":"0.0100"}
        """;

    internal const string SettlementJson = """
        {"event_ticker":"KXA-26OCT02","exchange_index":0,"fee_cost":"0.008400","market_result":"no","no_count_fp":"4.00","no_total_cost_dollars":"3.960000","revenue":0,
         "settled_time":"2026-10-03T13:09:17.122067Z","ticker":"KXA-1","value":0,"yes_count_fp":"4.00","yes_total_cost_dollars":"0.080000"}
        """;

    private const string QueueJson = """{"order_id":"o-1","market_ticker":"KXA-1","queue_position_fp":"12.00"}""";

    private static KalshiRestClient Signed(StubHttpMessageHandler stub)
    {
        var time = new AutoAdvanceTimeProvider();
        var key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
        return RestTestKit.Client(stub, time, signer: new KalshiSigner("kid", key, time));
    }

    private static string Page(string name, string cursor, params string[] items) => "{\"" + name + "\":[" + string.Join(",", items) + "],\"cursor\":\"" + cursor + "\"}";

    public static TheoryData<Type, string> Payloads() => new()
    {
        { typeof(AccountBalance), BalanceJson },
        { typeof(ShardBalance), """{"balance":"10.3490","exchange_index":0}""" },
        { typeof(MarketPosition), PositionJson },
        { typeof(EventPosition), EventPositionJson },
        { typeof(Order), OrderJson },
        { typeof(Fill), FillJson },
        { typeof(Settlement), SettlementJson },
        { typeof(QueuePosition), QueueJson },
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void TheModelsDeclareEveryFieldKalshiSends(Type model, string payload)
    {
        var declared = Declared(model);
        using var document = JsonDocument.Parse(payload);

        var missing = document.RootElement.EnumerateObject().Select(p => p.Name).Where(name => !declared.Contains(name)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void TheCheckHasTeeth()
    {
        Assert.DoesNotContain("new_kalshi_field", Declared(typeof(Order)));
    }

    /// <summary>The JSON names of a record's constructor parameters: snake case of the parameter name, or the name an attribute gives.</summary>
    private static HashSet<string> Declared(Type model)
    {
        var constructor = model.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return [.. constructor.GetParameters().Select(p =>
            model.GetProperty(p.Name!)!.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name!))];
    }

    [Fact]
    public async Task TheBalanceIsExactAndPerShard()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, BalanceJson);
        using var client = Signed(stub);

        var balance = await client.BalanceAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Base + "/portfolio/balance", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal((1034L, Dollars.Parse("10.3490"), 250L, 1791908624L), (balance.Balance, balance.BalanceDollars, balance.PortfolioValue, balance.UpdatedTs));
        Assert.Equal(Dollars.Parse("2.5"), balance.Shard(3));
        Assert.Equal(Dollars.Parse("10.349"), balance.Shard(0));
        Assert.Equal(Dollars.Zero, balance.Shard(2));   // not listed
        Assert.Equal(3, balance.BalanceBreakdown!.Count);
    }

    [Fact]
    public async Task TheBalanceCanBeNarrowedToAShardAndASubaccount()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"balance":5,"balance_dollars":"0.0500"}""");
        using var client = Signed(stub);

        var balance = await client.BalanceAsync(exchangeIndex: 3, subaccount: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Base + "/portfolio/balance?exchange_index=3&subaccount=1", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Null(balance.BalanceBreakdown);   // a key restricted to a subaccount has no breakdown
        Assert.Equal(Dollars.Zero, balance.Shard(3));
    }

    [Fact]
    public async Task PositionsParseAndALongNoPositionIsNegative()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"market_positions\":[" + PositionJson + "],\"event_positions\":[" + EventPositionJson + "],\"cursor\":\"\"}");
        using var client = Signed(stub);

        var page = await client.PositionsPageAsync(KalshiQuery.Empty.With("settlement_status", "all"), TestContext.Current.CancellationToken);

        Assert.Equal(Base + "/portfolio/positions?settlement_status=all", stub.Requests[0].Uri.AbsoluteUri);
        var position = Assert.Single(page.MarketPositions!);
        Assert.Equal((Contracts.Parse("-3.00"), Dollars.Parse("1.5"), Dollars.Parse("-0.1")), (position.PositionFp, position.MarketExposureDollars, position.RealizedPnlDollars));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), position.LastUpdatedTs);
        Assert.Equal(Dollars.Parse("1.5"), Assert.Single(page.EventPositions!).EventExposureDollars);
    }

    [Fact]
    public async Task AnAccountWithNoPositionsHasEmptyListsNotAnError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"market_positions":[],"event_positions":[],"cursor":""}""").Respond(HttpStatusCode.OK, "{}");
        using var client = Signed(stub);

        var empty = await client.PositionsPageAsync(cancellationToken: TestContext.Current.CancellationToken);
        var bare = await client.PositionsPageAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(empty.MarketPositions!);
        Assert.Null(bare.MarketPositions);
        Assert.Equal(string.Empty, bare.Cursor);
    }

    [Fact]
    public async Task OrdersFillsAndSettlementsFollowTheirCursors()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Page("orders", "c1", OrderJson))
            .Respond(HttpStatusCode.OK, Page("orders", string.Empty, OrderJson.Replace("01a0fb02-44a8-776b-9801-c464cfe31239", "o-2", StringComparison.Ordinal).Replace("\"resting\"", "\"executed\"", StringComparison.Ordinal)))
            .Respond(HttpStatusCode.OK, Page("fills", string.Empty, FillJson))
            .Respond(HttpStatusCode.OK, Page("settlements", string.Empty, SettlementJson))
            .Respond(HttpStatusCode.OK, "{\"market_positions\":[" + PositionJson + "],\"cursor\":\"\"}");
        using var client = Signed(stub);
        var token = TestContext.Current.CancellationToken;

        var orders = await ToListAsync(client.IterateOrdersAsync(KalshiQuery.Empty.With("limit", 1), token));
        var fills = await ToListAsync(client.IterateFillsAsync(cancellationToken: token));
        var settlements = await ToListAsync(client.IterateSettlementsAsync(cancellationToken: token));
        var positions = await ToListAsync(client.IteratePositionsAsync(cancellationToken: token));

        Assert.Equal(["01a0fb02-44a8-776b-9801-c464cfe31239", "o-2"], orders.Select(o => o.OrderId));
        Assert.Equal([true, false], orders.Select(o => o.IsResting));
        Assert.Equal(Base + "/portfolio/orders?limit=1&cursor=c1", stub.Requests[1].Uri.AbsoluteUri);
        var fill = Assert.Single(fills);
        Assert.Equal((Dollars.Parse("0.0007"), true, Contracts.Parse("1")), (fill.FeeCost, fill.IsTaker, fill.CountFp));
        var settlement = Assert.Single(settlements);
        Assert.Equal((Contracts.Parse("4"), 0L, "no"), (settlement.YesCountFp, settlement.Revenue, settlement.MarketResult));
        Assert.Equal(Contracts.Parse("-3"), Assert.Single(positions).PositionFp);
    }

    [Fact]
    public async Task AnOrderKeepsBothPricesAndTheOutcomeItTrades()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Page("orders", string.Empty, OrderJson));
        using var client = Signed(stub);

        var order = Assert.Single((await client.OrdersPageAsync(KalshiQuery.Empty.With("status", "resting"), TestContext.Current.CancellationToken)).Orders!);

        Assert.Equal(Base + "/portfolio/orders?status=resting", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal((Dollars.Parse("0.0100"), Dollars.Parse("0.9900"), "sell", "no", "yes", "ask"), (order.YesPriceDollars, order.NoPriceDollars, order.Action, order.OutcomeSide, order.Side, order.BookSide));
        Assert.Equal((Contracts.Parse("2"), Contracts.Parse("1"), Contracts.Parse("1")), (order.InitialCountFp, order.FillCountFp, order.RemainingCountFp));
        Assert.Equal((Dollars.Parse("0.0007"), "e2300918-c088-4100-992a-6eee2b09dd29", "taker_at_cross"), (order.TakerFeesDollars, order.ClientOrderId, order.SelfTradePreventionType));
        Assert.True(order.IsResting);
    }

    [Fact]
    public async Task OneOrderAndTheQueuePositions()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"order\":" + OrderJson.Replace("01a0fb02-44a8-776b-9801-c464cfe31239", "o-9", StringComparison.Ordinal) + "}")
            .Respond(HttpStatusCode.OK, "{\"queue_positions\":[" + QueueJson + "]}")
            .Respond(HttpStatusCode.OK, "{}");
        using var client = Signed(stub);
        var token = TestContext.Current.CancellationToken;

        var order = await client.OrderAsync("o-9", token);
        var queue = await client.QueuePositionsAsync(KalshiQuery.Empty.With("market_tickers", "KXA-1,KXB-1"), token);
        var none = await client.QueuePositionsAsync(cancellationToken: token);

        Assert.Equal("o-9", order.OrderId);
        Assert.Equal(Base + "/portfolio/orders/o-9", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Contracts.Parse("12"), Assert.Single(queue).QueuePositionFp);
        Assert.Equal(Base + "/portfolio/orders/queue_positions?market_tickers=KXA-1%2CKXB-1", stub.Requests[1].Uri.AbsoluteUri);
        Assert.Empty(none);
    }

    [Fact]
    public async Task EveryAccountReadWithoutASignerIsRefusedBeforeAnythingIsSent()
    {
        var stub = new StubHttpMessageHandler();
        using var client = RestTestKit.Client(stub, new AutoAdvanceTimeProvider());
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<AuthException>(() => client.BalanceAsync(cancellationToken: token));
        await Assert.ThrowsAsync<AuthException>(() => client.PositionsPageAsync(cancellationToken: token));
        await Assert.ThrowsAsync<AuthException>(() => client.OrdersPageAsync(cancellationToken: token));
        await Assert.ThrowsAsync<AuthException>(() => client.OrderAsync("o-1", token));
        await Assert.ThrowsAsync<AuthException>(() => client.FillsPageAsync(cancellationToken: token));
        await Assert.ThrowsAsync<AuthException>(() => client.SettlementsPageAsync(cancellationToken: token));
        await Assert.ThrowsAsync<AuthException>(() => client.QueuePositionsAsync(cancellationToken: token));
        Assert.Throws<AuthException>(() => client.IterateOrdersAsync(cancellationToken: token));
        Assert.Throws<AuthException>(() => client.IterateFillsAsync(cancellationToken: token));
        Assert.Throws<AuthException>(() => client.IterateSettlementsAsync(cancellationToken: token));
        Assert.Throws<AuthException>(() => client.IteratePositionsAsync(cancellationToken: token));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AccountReadsAreSignedLikeEveryOtherRequestAndStillOnlyGet()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, BalanceJson);
        using var client = Signed(stub);

        await client.BalanceAsync(cancellationToken: TestContext.Current.CancellationToken);

        var request = stub.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("kid", request.Headers[KalshiSigner.KeyHeader]);
        Assert.True(request.Headers.ContainsKey(KalshiSigner.SignatureHeader));
    }

    [Fact]
    public async Task AFieldWithTooManyDecimalsIsRefusedNotRounded()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"balance":1,"balance_dollars":"0.1234567"}""");
        using var client = Signed(stub);

        await Assert.ThrowsAnyAsync<Exception>(() => client.BalanceAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
