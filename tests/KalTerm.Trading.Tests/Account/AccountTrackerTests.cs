using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Trading.Account;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Tests.Risk;
using KalTerm.Trading.Tests.Transport;

namespace KalTerm.Trading.Tests.Account;

/// <summary>Kalshi's REST side, scripted: four shards, shard 2 not trading.</summary>
internal sealed class FakeAccountRest : IAccountRest
{
    public List<MarketPosition> Positions { get; set; } = [];

    public List<Order> Orders { get; set; } = [];

    public AccountBalance Balance { get; set; } = AccountTests.Balance("10.3490", "0.0000", "0.0000", "2.5000");

    public ExchangeStatus Status { get; set; } = AccountTests.StatusOf(transfers: true);

    public int BalanceCalls { get; set; }

    public Action<int>? OnBalanceRead { get; set; }

    public int FailBalance { get; set; }

    public int FailPositions { get; set; }

    public Task<ExchangeStatus> ExchangeStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

    public Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken)
    {
        BalanceCalls++;
        OnBalanceRead?.Invoke(BalanceCalls);
        if (FailBalance != 0)
        {
            FailBalance--;
            throw new HttpRequestException("network down");
        }

        return Task.FromResult(Balance);
    }

    public async IAsyncEnumerable<MarketPosition> PositionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (FailPositions != 0)
        {
            FailPositions--;
            throw new HttpRequestException("network down");
        }

        foreach (var position in Positions)
        {
            yield return position;
        }
    }

    public async IAsyncEnumerable<Order> RestingOrdersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var order in Orders.Where(o => o.Status == "resting"))
        {
            yield return order;
        }
    }
}

/// <summary>The private WebSocket, scripted: messages are pushed by the test; completing the stream is the socket closing for good.</summary>
internal sealed class FakeAccountSocket : IAccountSocket
{
    private readonly Channel<WsMessage> _queue = Channel.CreateUnbounded<WsMessage>();

    public List<string> Subscribed { get; } = [];

    public Task SubscribeAsync(string channel, CancellationToken cancellationToken)
    {
        Subscribed.Add(channel);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken) => _queue.Reader.ReadAllAsync(cancellationToken);

    public void Push(string type, string? json = null) =>
        _queue.Writer.TryWrite(new WsMessage(type, Msg: json is null ? null : JsonDocument.Parse(json).RootElement.Clone()));

    public void Close() => _queue.Writer.Complete();
}

internal sealed class TrackerFixture : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-tracker-" + Guid.NewGuid().ToString("N"));

    public TrackerFixture(FakeAccountRest? rest = null, TradingState? state = null, bool autoFireTime = false)
    {
        Rest = rest ?? new FakeAccountRest();
        Time = new AutoAdvanceTimeProvider(autoFireTime, AccountTests.T0);
        State = state ?? new TradingState(Path.Combine(_folder, "state.json"));
        Tracker = new AccountTracker(Rest, Socket, State, Time, TimeSpan.FromSeconds(5));
    }

    public FakeAccountRest Rest { get; }

    public FakeAccountSocket Socket { get; } = new();

    public AutoAdvanceTimeProvider Time { get; }

    public TradingState State { get; }

    public AccountTracker Tracker { get; }

    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition did not become true");
            }

            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Tracker.DisposeAsync();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

internal static class AccountTests
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public static AccountBalance Balance(params string[] amounts)
    {
        var rows = amounts.Select((a, i) => new ShardBalance(i, Dollars.Parse(a))).ToList();
        var total = rows.Aggregate(Dollars.Zero, (sum, r) => sum + r.Balance);
        return new AccountBalance(total.Micros / 10_000, total, 0, null, rows);
    }

    public static ExchangeStatus StatusOf(bool? transfers)
    {
        var rows = Enumerable.Range(0, 4).Select(i => new ExchangeIndexStatus(i, "s" + i, true, i != 2, transfers ?? false)).ToList();
        return new ExchangeStatus(true, true, null, rows);
    }

    public static MarketPosition Position(string ticker, string count, string exposure = "0", string realized = "0") =>
        new(ticker, 0, Contracts.Parse(count), null, Dollars.Parse(exposure), Dollars.Parse(realized));

    public static Order Order(string id = "o-1", string ticker = "KXA-1", string book = "bid", string yesPrice = "0.4000", string remaining = "2.00", string status = "resting")
    {
        var yes = Dollars.Parse(yesPrice);
        return new Order(id, ticker, status, book == "bid" ? "buy" : "sell", "yes", yes, Dollars.One - yes, Contracts.Parse("2.00"), Contracts.Zero, Contracts.Parse(remaining), BookSide: book);
    }

    public static string UserOrder(string id = "o-1", string status = "resting", string remaining = "2.00", string filled = "0.00", long updated = 1000, string book = "bid", string price = "0.4000") =>
        $$"""
        {"order_id":"{{id}}","client_order_id":"c-1","ticker":"KXA-1","exchange_index":0,"status":"{{status}}","side":"yes","outcome_side":"yes","book_side":"{{book}}",
         "yes_price_dollars":"{{price}}","initial_count_fp":"2.00","fill_count_fp":"{{filled}}","remaining_count_fp":"{{remaining}}","last_updated_ts_ms":{{updated}}}
        """;
}

public class KalshiAccountRestTests
{
    [Fact]
    public async Task TheRealRestAdapterReadsStatusBalancePositionsAndRestingOrders()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(System.Net.HttpStatusCode.OK, """{"exchange_active":true,"trading_active":true,"exchange_index_statuses":[{"exchange_index":0,"description":"s0","exchange_active":true,"trading_active":true}]}""")
            .Respond(System.Net.HttpStatusCode.OK, """{"balance":1034,"balance_dollars":"10.3490","balance_breakdown":[{"exchange_index":0,"balance":"10.3490"}]}""")
            .Respond(System.Net.HttpStatusCode.OK, """{"market_positions":[{"ticker":"KXA-1","position_fp":"3.00"}],"event_positions":[],"cursor":""}""")
            .Respond(System.Net.HttpStatusCode.OK, """{"orders":[],"cursor":""}""");
        using var signer = TransportKit.Signer();
        using var client = new KalTerm.Kalshi.Rest.KalshiRestClient(KalTerm.Kalshi.Configuration.KalshiSettings.Default, new KalTerm.Kalshi.Rest.KalshiRestClientOptions { Signer = signer.Signer }, stub);
        var rest = new KalshiAccountRest(client);
        var ct = TestContext.Current.CancellationToken;

        var status = await rest.ExchangeStatusAsync(ct);
        var balance = await rest.BalanceAsync(ct);
        var positions = await rest.PositionsAsync(ct).ToListAsync(ct);
        var orders = await rest.RestingOrdersAsync(ct).ToListAsync(ct);

        Assert.True(status.ExchangeActive);
        Assert.Equal(10_349_000, balance.Shard(0).Micros);
        Assert.Equal(300, Assert.Single(positions).PositionFp.Hundredths);
        Assert.Empty(orders);
        Assert.Contains("settlement_status=all", stub.Requests[2].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("status=resting", stub.Requests[3].Uri.Query, StringComparison.Ordinal);
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }
}

public class TrackedOrderTests
{
    [Theory]
    [InlineData("bid", true, TradeAction.Buy)]
    [InlineData("ask", false, TradeAction.Sell)]
    public void WhichWayARestingOrderPushesThePositionFollowsTheBookSideAlone(string book, bool addsYes, TradeAction action)
    {
        var order = new TrackedOrder("o", "c", "T", 0, "resting", book, 400_000, 200, 0, 200, 0);

        Assert.Equal((addsYes, action, true), (order.AddsYes, order.Action, order.IsResting));
    }
}

public class AccountTrackerTests
{
    [Fact]
    public async Task NothingIsReportedBeforeTheFirstFullRead()
    {
        await using var fixture = new TrackerFixture();

        Assert.Null(fixture.Tracker.Snapshot());
        Assert.False(fixture.Tracker.Synced);
    }

    [Fact]
    public async Task StartSubscribesReadsEverythingAndBuildsAnExactSnapshot()
    {
        var rest = new FakeAccountRest
        {
            Positions = [AccountTests.Position("KXA-1", "3.00", "1.20", "-0.25"), AccountTests.Position("KXB-1", "-1.00", "0.40")],
            Orders = [AccountTests.Order("o-1", "KXA-1", "bid", "0.4000", "2.00"), AccountTests.Order("o-2", "KXB-1", "ask", "0.3000", "1.00"), AccountTests.Order("o-3", status: "canceled")],
        };
        await using var fixture = new TrackerFixture(rest);

        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fill", "user_orders", "market_positions"], fixture.Socket.Subscribed);
        var snap = fixture.Tracker.Snapshot()!;
        Assert.True(snap.StreamOk);
        Assert.False(snap.Halted);
        // shard 0 holds 10.349 in cash, of which 0.80 + 0.70 may be needed by the resting orders
        Assert.Equal([8_849_000L, 0, 0, 2_500_000], snap.Shards.OrderBy(s => s.Key).Select(s => s.Value.BalanceE6));
        Assert.Equal([true, true, false, true], snap.Shards.OrderBy(s => s.Key).Select(s => s.Value.TradingActive));
        Assert.All(snap.Shards.Values, s => Assert.True(s.TransfersActive));
        Assert.Equal(2, snap.OpenOrders);   // the cancelled one is not counted
        Assert.Equal((300L, 200L, 0L), (snap.Markets["KXA-1"].PositionE2, snap.Markets["KXA-1"].RestingYesE2, snap.Markets["KXA-1"].RestingNoE2));
        Assert.Equal((-100L, 0L, 100L), (snap.Markets["KXB-1"].PositionE2, snap.Markets["KXB-1"].RestingYesE2, snap.Markets["KXB-1"].RestingNoE2));
        // positions 1.20 + 0.40, the bid 2 x 0.40, and the ask at 0.30 (selling YES that is not held: (1 - 0.30) of collateral)
        Assert.Equal(1_200_000 + 400_000 + 800_000 + 700_000, snap.ExposureE6);
        Assert.Equal(AccountTests.T0, snap.AsOf);
    }

    [Fact]
    public async Task MissingTransferFlagsMeanFundingIsNotAllowed()
    {
        await using var fixture = new TrackerFixture(new FakeAccountRest { Status = AccountTests.StatusOf(transfers: null) });

        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(fixture.Tracker.Snapshot()!.Shards.Values, s => s.TransfersActive);
    }

    [Fact]
    public async Task OrderMessagesReplaceTheOrderAndOlderOnesAreIgnored()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.Socket.Push("user_order", AccountTests.UserOrder(updated: 1000));
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Orders.Count == 1);
        fixture.Socket.Push("user_order", AccountTests.UserOrder(remaining: "1.00", filled: "1.00", updated: 2000));
        fixture.Socket.Push("user_order", AccountTests.UserOrder(updated: 1500));   // stale: arrived late
        fixture.Socket.Push("user_order", AccountTests.UserOrder(id: "marker", status: "canceled", updated: 1));
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Orders.Count == 2);

        Assert.Equal([(100L, 100L)], fixture.Tracker.Orders.Where(o => o.OrderId == "o-1").Select(o => (o.RemainingE2, o.FillE2)));
        fixture.Socket.Push("user_order", AccountTests.UserOrder(status: "canceled", updated: 3000));
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Snapshot()!.OpenOrders == 0);
    }

    [Fact]
    public async Task FinishedOrdersAreTrimmedButRestingOnesAreNeverDropped()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        for (var n = 0; n < 3; n++)
        {
            fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder(id: "live-" + n, updated: 1)));
        }

        for (var n = 0; n < 1200; n++)
        {
            fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder(id: "done-" + n, status: "canceled", remaining: "0.00", updated: 2 + n)));
        }

        Assert.Equal(["live-0", "live-1", "live-2"], fixture.Tracker.Orders.Where(o => o.IsResting).Select(o => o.OrderId).Order());
        Assert.InRange(fixture.Tracker.Orders.Count, 1, 1000);
    }

    [Fact]
    public async Task ARestingOrderWithNothingLeftAddsNothingToTheLimits()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder(id: "live", updated: 1)));
        fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder(id: "spent", remaining: "0.00", filled: "2.00", updated: 1)));

        Assert.Equal(200, fixture.Tracker.Snapshot()!.Markets["KXA-1"].RestingYesE2);
    }

    [Fact]
    public async Task PositionMessagesReplaceTheMarketsPositionKeepingFiguresNotSent()
    {
        await using var fixture = new TrackerFixture(new FakeAccountRest { Positions = [AccountTests.Position("KXA-1", "1.00", "0.40", "0.10")] });
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"-4.00","position_cost_dollars":"1.9"}"""));
        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXNEW-1","position_fp":"2.00","realized_pnl_dollars":"-0.5"}"""));

        Assert.Equal(new TrackedPosition(-400, 1_900_000, 100_000), fixture.Tracker.Positions["KXA-1"]);
        Assert.Equal(-500_000, fixture.Tracker.Positions["KXNEW-1"].RealizedE6);
    }

    [Fact]
    public async Task AFillIsCountedOnceAndMakesTheBalanceBeReadAtOnce()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        var before = fixture.Rest.BalanceCalls;
        const string Fill = """{"trade_id":"t-1","order_id":"o-1","market_ticker":"KXA-1","yes_price_dollars":"0.4000","count_fp":"1.00","fee_cost":"0.0200"}""";

        fixture.Socket.Push("fill", Fill);
        fixture.Socket.Push("fill", Fill);   // duplicate delivery
        await TrackerFixture.UntilAsync(() => fixture.Rest.BalanceCalls > before);
        fixture.Socket.Push("fill", Fill.Replace("t-1", "t-2", StringComparison.Ordinal));
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Fills.Count == 2);

        Assert.Equal(["t-1", "t-2"], fixture.Tracker.Fills.Select(f => f.TradeId));
        Assert.Equal(20_000, fixture.Tracker.Fills[0].FeeCost.Micros);
    }

    [Fact]
    public async Task AfterAReconnectTheStreamIsNotOkUntilEverythingIsReadAgain()
    {
        var rest = new FakeAccountRest { Positions = [AccountTests.Position("KXA-1", "1.00")] };
        await using var fixture = new TrackerFixture(rest);
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Tracker.Snapshot()!.StreamOk);

        // while the link was down the world changed: a fill moved the position, an order went
        rest.Positions = [AccountTests.Position("KXA-1", "5.00")];
        rest.Orders = [AccountTests.Order("o-9")];
        rest.FailPositions = 2;   // the re-reads fail for a while; the tracker keeps trying
        fixture.Socket.Push("reconnected", """{"resubscribed":[],"failed":[],"reason":"connection_lost","dropped":0}""");
        fixture.Socket.Push("user_order", AccountTests.UserOrder(id: "o-9", remaining: "1.00", updated: 5000));   // sent right after the reconnect: applied after the re-read
        await TrackerFixture.UntilAsync(() =>
        {
            if (fixture.Time.PendingTimers > 0)
            {
                fixture.Time.FireNext();   // the poll's wait or the resync's retry pause, whichever is oldest
            }

            return rest.FailPositions == 0 && fixture.Tracker.Synced;
        });

        var snap = fixture.Tracker.Snapshot()!;
        Assert.True(snap.StreamOk);
        Assert.Equal(500, snap.Markets["KXA-1"].PositionE2);
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Orders.Any(o => o.OrderId == "o-9" && o.RemainingE2 == 100));
    }

    [Fact]
    public async Task DuringTheReReadTheSnapshotSaysTheStreamIsDown()
    {
        var rest = new FakeAccountRest();
        await using var fixture = new TrackerFixture(rest);   // manual time: the resync retry pause never ends
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        rest.FailPositions = 1000;

        fixture.Socket.Push("reconnected", """{"resubscribed":[],"failed":[],"reason":"x","dropped":0}""");
        await TrackerFixture.UntilAsync(() => !fixture.Tracker.Synced);

        var down = fixture.Tracker.Snapshot()!;
        Assert.False(down.StreamOk);   // the risk layer would refuse now
        var verdict = RiskTestKit.Verdict(snapshot: down);
        Assert.Contains(RiskCode.StreamDown, verdict.Refusals.Select(r => r.Code));
    }

    [Fact]
    public async Task TheSnapshotSaysTheStreamIsDownWhenTheSocketClosesForGood()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.Socket.Close();
        await TrackerFixture.UntilAsync(() => !fixture.Tracker.Snapshot()!.StreamOk);
    }

    [Fact]
    public async Task AnUnreadableMessageFailsTheStreamClosedAndSaysWhy()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.Socket.Push("fill", """{"trade_id":"t","order_id":"o","market_ticker":"K","yes_price_dollars":"0.0000001","count_fp":"1.00"}""");
        await TrackerFixture.UntilAsync(() => !fixture.Tracker.Snapshot()!.StreamOk);

        Assert.NotEmpty(fixture.Tracker.Fault);
    }

    [Fact]
    public async Task ABalanceThatCannotBeRefreshedGoesStaleAndTheRiskLayerRefuses()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        await TrackerFixture.UntilAsync(() => fixture.Time.PendingTimers > 0);
        fixture.Rest.FailBalance = 1_000_000;   // every balance poll fails from now on

        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Time.FireNext();
        await TrackerFixture.UntilAsync(() => fixture.Tracker.Fault.Contains("balance poll failed", StringComparison.Ordinal));

        var snap = fixture.Tracker.Snapshot()!;
        Assert.Equal(AccountTests.T0, snap.AsOf);   // the last good read
        var verdict = RiskChecker.Check(RiskTestKit.Order(), snap, new RiskPolicy(), fixture.Time.GetUtcNow(), production: false);
        Assert.Contains(RiskCode.StaleSnapshot, verdict.Refusals.Select(r => r.Code));
    }

    [Fact]
    public async Task TheDaysLossIsMeasuredFromTheBaselineAndSurvivesARestart()
    {
        var rest = new FakeAccountRest { Positions = [AccountTests.Position("KXA-1", "2.00", realized: "1.00")] };
        var path = Path.Combine(Path.GetTempPath(), "kalterm-loss-" + Guid.NewGuid().ToString("N"), "state.json");
        try
        {
            await using (var fixture = new TrackerFixture(rest, new TradingState(path)))
            {
                await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, fixture.Tracker.Snapshot()!.RealizedPnlTodayE6);

                fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"0","realized_pnl_dollars":"-0.75"}"""));

                Assert.Equal(-1_750_000, fixture.Tracker.Snapshot()!.RealizedPnlTodayE6);
            }

            // restarted later the same day: the baseline is not taken again from the new total
            rest.Positions = [AccountTests.Position("KXA-1", "0.00", realized: "-0.75")];
            await using var again = new TrackerFixture(rest, TradingState.Load(path));
            await again.Tracker.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(-1_750_000, again.Tracker.Snapshot()!.RealizedPnlTodayE6);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ANewDayStartsFromZero()
    {
        var rest = new FakeAccountRest { Positions = [AccountTests.Position("KXA-1", "0.00", realized: "-3.00")] };
        var path = Path.Combine(Path.GetTempPath(), "kalterm-day-" + Guid.NewGuid().ToString("N"), "state.json");
        try
        {
            var state = new TradingState(path);
            state.Roll("2026-10-12");
            state.SetBaseline(0);
            state.BeginTransfer("t", 0, 1, 1_000_000);
            await using var fixture = new TrackerFixture(rest, state);

            await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

            var snap = fixture.Tracker.Snapshot()!;
            Assert.Equal((0L, 0L), (snap.RealizedPnlTodayE6, snap.TransferredTodayE6));   // yesterday's transfer no longer counts
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task TheHaltAndTheDaysTransfersReachTheSnapshot()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        fixture.State.Halt("test");
        fixture.State.BeginTransfer("t", 0, 3, 700_000);

        var snap = fixture.Tracker.Snapshot()!;
        Assert.True(snap.Halted);
        Assert.Equal(700_000, snap.TransferredTodayE6);
    }

    [Fact]
    public async Task AFailedFirstReadThrowsAndLeavesNothingSynced()
    {
        await using var fixture = new TrackerFixture(new FakeAccountRest { FailBalance = 1 });

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Tracker.StartAsync(TestContext.Current.CancellationToken));

        Assert.Null(fixture.Tracker.Snapshot());
    }

    [Fact]
    public async Task TheChangeHookFiresWhenTheViewMayDifferAndABrokenListenerIsHarmless()
    {
        await using var fixture = new TrackerFixture(new FakeAccountRest { Positions = [AccountTests.Position("KXA-1", "1.00")] });
        var calls = 0;
        fixture.Tracker.Changed += () => Interlocked.Increment(ref calls);
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        var afterStart = Volatile.Read(ref calls);
        Assert.True(afterStart >= 2);   // the balance read and the full read

        fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder()));
        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"2.00"}"""));
        fixture.Tracker.Apply(Message("fill", """{"trade_id":"t-1","order_id":"o-1","market_ticker":"KXA-1","yes_price_dollars":"0.4","count_fp":"1.00"}"""));
        Assert.True(Volatile.Read(ref calls) >= afterStart + 3);

        var before = Volatile.Read(ref calls);
        fixture.Tracker.Apply(Message("subscribed", "{}"));   // not an account message
        Assert.Equal(before, Volatile.Read(ref calls));

        fixture.Tracker.Changed += () => throw new InvalidOperationException("a listener's own bug");
        fixture.Tracker.Apply(Message("user_order", AccountTests.UserOrder(id: "o-2", updated: 5000)));
        Assert.Contains(fixture.Tracker.Orders, o => o.OrderId == "o-2");
    }

    [Fact]
    public async Task StoppingEndsTheBackgroundWorkAndReportsTheStreamDown()
    {
        await using var fixture = new TrackerFixture();
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);

        await fixture.Tracker.StopAsync();

        Assert.False(fixture.Tracker.Snapshot()!.StreamOk);
    }

    private static WsMessage Message(string type, string json) => new(type, Msg: JsonDocument.Parse(json).RootElement.Clone());
}
