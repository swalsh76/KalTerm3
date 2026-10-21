using System.Text.Json;
using KalTerm.Core.Fees;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Tests.Account;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Tests.Orders;

internal sealed class ServiceFixture : IAsyncDisposable
{
    private readonly TrackerFixture _tracker;
    private int _ids;

    public ServiceFixture(RiskPolicy? policy = null, bool production = false, FakeAccountRest? rest = null)
    {
        Rest = rest ?? new FakeAccountRest { Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000") };
        _tracker = new TrackerFixture(Rest);
        Markets = new FakeMarketRest(Rest);
        Trading = new FakeTrading(Rest, _tracker.State);
        Time = new InstantTimeProvider();
        Service = new OrderService(Markets, Trading, _tracker.Tracker, policy ?? new RiskPolicy(), production, Time, () => "id-" + _ids++);
    }

    public FakeAccountRest Rest { get; }

    public FakeMarketRest Markets { get; }

    public FakeTrading Trading { get; }

    public InstantTimeProvider Time { get; }

    public OrderService Service { get; }

    public AccountTracker Tracker => _tracker.Tracker;

    public TradingState State => _tracker.State;

    public FakeAccountSocket Socket => _tracker.Socket;

    public static async Task<ServiceFixture> StartAsync(RiskPolicy? policy = null, bool production = false, FakeAccountRest? rest = null)
    {
        var fixture = new ServiceFixture(policy, production, rest);
        await fixture.Tracker.StartAsync(TestContext.Current.CancellationToken);
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await _tracker.DisposeAsync();
    }
}

public class OrderServiceTests
{
    private static TradeRequest Buy(string ticker = "KXA-1", long count = 200, long price = 400_000, TradeOutcome outcome = TradeOutcome.Yes, bool postOnly = false) =>
        new(ticker, TradeAction.Buy, outcome, count, price, PostOnly: postOnly);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Order Resting(string id, string ticker = "KXA-1") => AccountData.Order(id, ticker);

    [Theory]
    [InlineData(TradeOutcome.Yes, TradeAction.Buy, 400_000, BookSide.Bid, 400_000)]
    [InlineData(TradeOutcome.Yes, TradeAction.Sell, 400_000, BookSide.Ask, 400_000)]
    [InlineData(TradeOutcome.No, TradeAction.Buy, 300_000, BookSide.Ask, 700_000)]
    [InlineData(TradeOutcome.No, TradeAction.Sell, 300_000, BookSide.Bid, 700_000)]
    public void OrdersAreExpressedOnTheYesBook(TradeOutcome outcome, TradeAction action, long own, BookSide side, long yesPrice)
    {
        Assert.Equal((side, yesPrice), OrderService.ToV2(outcome, action, own));
    }

    [Theory]
    [InlineData("quadratic", "1", 1_000_000)]
    [InlineData("quadratic", "0.5", 500_000)]
    [InlineData("quadratic_with_maker_fees", "0.07", 70_000)]
    [InlineData("flat", "1", null)]
    [InlineData(null, null, null)]
    [InlineData("quadratic", null, null)]
    public async Task FeeMultipliersComeFromTheSeriesAndUnmodellableOnesAreUnknown(string? feeType, string? multiplier, int? expected)
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Markets.FeeType = feeType;
        fixture.Markets.FeeMultiplier = multiplier is null ? null : FeeMultiplier.Parse(multiplier);

        var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);

        Assert.Equal(expected, prepared.Intent.FeeMultiplierE6);
    }

    [Fact]
    public async Task PrepareIsReadOnlyAndSaysWhatWouldHappen()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);

        Assert.True(prepared.Verdict.Ok);
        Assert.Equal((0, 1_000_000L, BookSide.Bid, 400_000L), (prepared.Market.ExchangeIndex, prepared.Intent.FeeMultiplierE6, prepared.Side, prepared.PriceE6));
        Assert.Empty(fixture.Trading.Calls);
        var lines = OrderService.Describe(prepared);
        Assert.Equal("BUY 2 YES KXA-1 at $0.4000 limit (good_till_canceled), shard 0", lines[0]);
        Assert.StartsWith("worst-case cost $0.8", lines[1], StringComparison.Ordinal);   // 2 x 0.40 plus the fee
        Assert.Equal(2, lines.Count);   // shard 0 holds the money: no transfer
    }

    [Fact]
    public async Task TheMarketAndItsFeeAreLookedUpOnce()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        await fixture.Service.PrepareAsync(Buy(), Ct);
        await fixture.Service.PrepareAsync(Buy(count: 100), Ct);

        Assert.Equal(1, fixture.Markets.SeriesCalls);
    }

    [Fact]
    public async Task AShardWithoutMoneyShowsTheTransferItWouldMake()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);

        Assert.True(prepared.Verdict.Ok);
        Assert.Equal(3, prepared.Market.ExchangeIndex);
        var transfer = Assert.Single(prepared.Verdict.Funding);
        Assert.Equal((0, 3), (transfer.Source, transfer.Destination));
        Assert.Contains(OrderService.Describe(prepared), line => line.StartsWith("first moves $", StringComparison.Ordinal) && line.Contains("from shard 0 to shard 3", StringComparison.Ordinal));
        Assert.Empty(fixture.Trading.Calls);
    }

    [Theory]
    [InlineData("closed", RiskCode.MarketNotTradable)]
    [InlineData("flat", RiskCode.UnknownFee)]
    [InlineData("noshard", RiskCode.UnknownShard)]
    public async Task AMarketThatCannotBeTradedOrPricedIsRefusedBeforeAnythingMoves(string setup, RiskCode code)
    {
        await using var fixture = await ServiceFixture.StartAsync();
        switch (setup)
        {
            case "closed":
                fixture.Markets.Markets["KXA-1"] = FakeMarketRest.MarketOf("KXA-1", 0, "closed");
                break;
            case "flat":
                fixture.Markets.FeeType = "flat";
                break;
            default:
                fixture.Markets.Markets["KXA-1"] = FakeMarketRest.MarketOf("KXA-1", null);
                break;
        }

        var thrown = await Assert.ThrowsAsync<OrderRefusedException>(async () => await fixture.Service.ExecuteAsync(await fixture.Service.PrepareAsync(Buy(), Ct), Ct));

        Assert.Contains(code, thrown.Verdict!.Refusals.Select(r => r.Code));
        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task ExecutePlacesExactlyTheOrderThatWasShown()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy(postOnly: true), Ct);

        var placement = await fixture.Service.ExecuteAsync(prepared, Ct);

        Assert.Equal(["create"], fixture.Trading.Names);
        var sent = (OrderRequest)fixture.Trading.Calls[0].Arg!;
        Assert.Equal(("KXA-1", BookSide.Bid, 200L, 400_000L, 0, true, "id-0", TimeInForce.GoodTillCanceled), (sent.Ticker, sent.Side, sent.CountE2, sent.PriceE6, sent.ExchangeIndex, sent.PostOnly, sent.ClientOrderId, sent.TimeInForce));
        Assert.Equal(("o-new", "id-0"), (placement.Created.OrderId, placement.ClientOrderId));
        Assert.Equal(prepared.Intent, placement.Approved.Intent);
        Assert.Empty(placement.Transfers);
    }

    [Fact]
    public async Task ANoOrderIsSentAsTheOppositeSideOfTheYesBook()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        await fixture.Service.ExecuteAsync(await fixture.Service.PrepareAsync(Buy(outcome: TradeOutcome.No, price: 300_000), Ct), Ct);

        var sent = (OrderRequest)fixture.Trading.Calls[0].Arg!;
        Assert.Equal((BookSide.Ask, 700_000L), (sent.Side, sent.PriceE6));   // buy NO at 30c is sell YES at 70c
    }

    [Fact]
    public async Task FundingHappensBeforeTheOrderAndIsCountedAndRecorded()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);

        var placement = await fixture.Service.ExecuteAsync(prepared, Ct);

        Assert.Equal(["transfer", "create"], fixture.Trading.Names);
        var (source, destination, amount, transferId) = ((int, int, long, string))fixture.Trading.Calls[0].Arg!;
        Assert.Equal((0, 3, "id-0"), (source, destination, transferId));
        Assert.Equal(placement.Transfers[0].AmountE6, amount);
        Assert.True(amount >= (prepared.Verdict.WorstCaseCostE6 ?? 0));
        var created = (OrderRequest)fixture.Trading.Calls[1].Arg!;
        Assert.Equal(("id-1", 3), (created.ClientOrderId, created.ExchangeIndex));
        Assert.Equal(amount, fixture.State.TransferredE6);
        Assert.Empty(fixture.State.PendingTransfers);
        Assert.Equal(amount, TradingState.Load(fixture.State.Day.Length == 0 ? null : StatePath(fixture)).TransferredE6);
    }

    private static string StatePath(ServiceFixture fixture) => (string)typeof(TradingState).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fixture.State)!;

    [Fact]
    public async Task ARefusedOrderSendsNothingAtAll()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy(count: 5_000, price: 900_000), Ct);   // far over every limit
        Assert.False(prepared.Verdict.Ok);

        var thrown = await Assert.ThrowsAsync<OrderRefusedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Contains(RiskCode.OrderTooLarge, thrown.Verdict!.Refusals.Select(r => r.Code));
        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task APriceKalshiCannotTakeFailsBeforeAnyMoneyMoves()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PrepareAsync(Buy("KXT-1", price: 123_456), Ct));

        Assert.Contains("decimal places", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Trading.Calls);
        Assert.Equal(0, fixture.State.TransferredE6);
    }

    [Fact]
    public async Task IfThePlanChangedSinceItWasShownNothingIsSent()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);
        Assert.NotEmpty(prepared.Verdict.Funding);
        fixture.Rest.Balance = AccountData.Balance("0.0000", "10.3490", "0.0000", "0.0000");   // the money moved while the person read the prompt

        await Assert.ThrowsAsync<PlanChangedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task ARefusedTransferStopsTheOrderAndIsNotCounted()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Trading.TransferError = new KalshiApiException(400, "no");
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);

        await Assert.ThrowsAsync<TransferFailedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Equal(["transfer"], fixture.Trading.Names);
        Assert.Equal(0, fixture.State.TransferredE6);
        Assert.Empty(fixture.State.PendingTransfers);
    }

    [Fact]
    public async Task ATransferWithNoClearAnswerStaysPendingAndStopsTheOrder()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Trading.TransferError = new TimeoutException("slow");
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);

        await Assert.ThrowsAsync<TransferUnknownException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Equal(["transfer"], fixture.Trading.Names);
        Assert.True(fixture.State.TransferredE6 > 0);
        Assert.Single(fixture.State.PendingTransfers);
    }

    [Fact]
    public async Task TransfersThatDoNotArriveStopTheOrder()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);
        fixture.Trading.TransferOverride = (_, _, _, _) => Task.FromResult("ok");   // Kalshi said yes but the balance did not move

        var thrown = await Assert.ThrowsAsync<FundingShortException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Contains("has not shown up", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("kterm balance", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["transfer"], fixture.Trading.Names);
        Assert.True(fixture.State.TransferredE6 > 0);   // still counted
    }

    [Fact]
    public async Task ABalanceThatCatchesUpSlowlyAfterATransferStillGetsTheOrderPlaced()
    {
        // seen live 2026-10-13: the read right after a transfer still showed the old shard balance
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1", count: 100, price: 10_000), Ct);
        var after = AccountData.Balance("10.3290", "0.0000", "0.0000", "0.0200");
        var before = fixture.Rest.BalanceCalls;
        fixture.Trading.TransferOverride = (s, d, a, id) =>
        {
            fixture.Rest.BalanceCalls = 0;
            fixture.Rest.OnBalanceRead = reads => { if (reads > 3) { fixture.Rest.Balance = after; } };   // three stale reads, then the truth
            return Task.FromResult("ok");
        };
        _ = before;

        var placement = await fixture.Service.ExecuteAsync(prepared, Ct);

        Assert.Equal(["transfer", "create"], fixture.Trading.Names);
        Assert.NotEmpty(placement.Transfers);
        Assert.True(fixture.State.TransferredE6 > 0);
        Assert.Equal([0.5, 0.5, 0.5], fixture.Time.Delays.Select(d => d.TotalSeconds));   // a pause between polls, not a spin
    }

    [Fact]
    public async Task AnOrderThatBecameDisallowedWhileWaitingIsRefusedNotWaitedOn()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var prepared = await fixture.Service.PrepareAsync(Buy("KXT-1"), Ct);
        fixture.Trading.TransferOverride = (_, _, _, _) =>
        {
            fixture.State.Halt("kill switch");
            return Task.FromResult("ok");
        };

        var thrown = await Assert.ThrowsAsync<FundingShortException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Contains("no longer allowed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("halted", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("create", fixture.Trading.Names);
    }

    [Fact]
    public async Task AnOrderWithNoClearAnswerIsRememberedByItsId()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Trading.CreateError = new WriteOutcomeUnknownException("create_order", "id-0");
        var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);

        await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.Equal(["id-0"], fixture.State.UnknownOrders);
    }

    [Fact]
    public async Task ProductionIsRefusedUnlessThePolicyAllowsIt()
    {
        await using (var fixture = await ServiceFixture.StartAsync(production: true))
        {
            var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);
            Assert.Contains(RiskCode.ProductionDisabled, prepared.Verdict.Refusals.Select(r => r.Code));
            await Assert.ThrowsAsync<OrderRefusedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));
            Assert.Empty(fixture.Trading.Calls);
        }

        await using var allowed = await ServiceFixture.StartAsync(new RiskPolicy { ProductionEnabled = true }, production: true);
        await allowed.Service.ExecuteAsync(await allowed.Service.PrepareAsync(Buy(), Ct), Ct);
        Assert.Equal(["create"], allowed.Trading.Names);
    }

    [Fact]
    public async Task AHaltedAccountRefusesNewOrdersButNotCancels()
    {
        var rest = new FakeAccountRest { Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000"), Orders = [Resting("o-1")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        fixture.State.Halt("kill switch");

        var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);
        Assert.Contains(RiskCode.Halted, prepared.Verdict.Refusals.Select(r => r.Code));
        await Assert.ThrowsAsync<OrderRefusedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));
        await fixture.Service.CancelAsync("o-1", Ct);

        Assert.Equal(["cancel"], fixture.Trading.Names);
        Assert.Equal(new CancelTarget("o-1", 0, "KXA-1"), fixture.Trading.Calls[0].Arg);
    }

    [Fact]
    public async Task CancellingAnUnknownOrderIsAnError()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        await Assert.ThrowsAsync<OrderNotFoundException>(() => fixture.Service.CancelAsync("nope", Ct));

        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task EveryVerdictAndKillIsWrittenToTheAuditWithoutSecrets()
    {
        var recorded = new List<string>();
        var rest = new FakeAccountRest { Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000") };
        var tracker = new TrackerFixture(rest);
        await tracker.Tracker.StartAsync(Ct);
        var markets = new FakeMarketRest(rest);
        var trading = new FakeTrading(rest, tracker.State);
        await using var service = new OrderService(markets, trading, tracker.Tracker, new RiskPolicy(), false, new InstantTimeProvider(), () => "id", new ListAudit(recorded));

        await service.ExecuteAsync(await service.PrepareAsync(Buy(), Ct), Ct);
        await service.KillAsync("test", Ct);
        await tracker.DisposeAsync();

        Assert.Contains(recorded, entry => entry.StartsWith("risk.prepare KXA-1 approved | BUY ", StringComparison.Ordinal) && entry.Contains(" YES at $", StringComparison.Ordinal) && entry.Contains("worst-case $", StringComparison.Ordinal));
        Assert.Contains(recorded, entry => entry.StartsWith("risk.execute KXA-1 approved | BUY ", StringComparison.Ordinal));
        Assert.Contains("kill all test", recorded);
    }

    private sealed class ListAudit(List<string> into) : ITradingAudit
    {
        public void Record(DateTimeOffset at, string what, string reference, int? status, string? detail) => into.Add($"{what} {reference} {detail}");
    }

    [Fact]
    public async Task KillHaltsFirstThenCancelsAndChecksUntilNothingRests()
    {
        var rest = new FakeAccountRest { Orders = [Resting("o-1"), Resting("o-2")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);

        var report = await fixture.Service.KillAsync("test", Ct);

        Assert.True(fixture.Trading.HaltedWhenCancelAll);   // the halt was already saved
        Assert.True(report.Clean);
        Assert.Equal(["cancel_all"], fixture.Trading.Names);
        Assert.Empty(rest.Orders);
    }

    [Fact]
    public async Task KillCancelsAnOrderThatAppearsAfterTheCancelAll()
    {
        var rest = new FakeAccountRest { Orders = [Resting("o-1")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        fixture.Trading.LateOrders = [Resting("late-1")];

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.Equal(["cancel_all", "cancel_batch"], fixture.Trading.Names);
        Assert.Equal(["late-1"], (List<string>)fixture.Trading.Calls[1].Arg!);
        Assert.True(report.Clean);
        Assert.Equal(["late-1"], report.Cancelled);
    }

    [Fact]
    public async Task KillNeedsTwoEmptyLooksBeforeItCallsTheJobDone()
    {
        var rest = new FakeAccountRest();
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        var looks = 0;
        fixture.Markets.Listing = () =>
        {
            looks++;
            if (looks == 2)
            {
                rest.Orders = [Resting("sneaky")];   // an order appears right after a look that found nothing
            }

            return rest.RestingOrdersAsync(Ct);
        };

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.Equal(("cancel_batch", (object)new List<string> { "sneaky" }), (fixture.Trading.Calls[^1].Name, (object)(List<string>)fixture.Trading.Calls[^1].Arg!), new BatchComparer());
        Assert.True(report.Clean);
        Assert.True(looks >= 4);
    }

    private sealed class BatchComparer : IEqualityComparer<(string, object)>
    {
        public bool Equals((string, object) x, (string, object) y) => x.Item1 == y.Item1 && ((List<string>)x.Item2).SequenceEqual((List<string>)y.Item2);

        public int GetHashCode((string, object) obj) => obj.Item1.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillStillHaltsAndCancelsWhenCancelAllFails()
    {
        var rest = new FakeAccountRest { Orders = [Resting("o-1"), Resting("o-2")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        fixture.Trading.CancelAllError = new TimeoutException("down");
        fixture.Trading.CancelAllClears = false;

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.True(fixture.State.Halted);
        Assert.Equal(["cancel_all", "cancel_batch"], fixture.Trading.Names);
        Assert.Empty(rest.Orders);
        Assert.Equal(["o-1", "o-2"], report.Cancelled);
        Assert.False(report.Clean);
        Assert.Contains("cancel-all", report.Errors[0], StringComparison.Ordinal);   // reported honestly
    }

    [Fact]
    public async Task KillReportsOrdersThatWillNotCancel()
    {
        var rest = new FakeAccountRest { Orders = [Resting("stuck")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        fixture.Trading.CancelAllClears = false;
        fixture.Trading.CancelNothing = true;

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.False(report.Clean);
        Assert.Equal(["stuck"], report.Remaining);
        Assert.Equal(OrderService.KillPasses, fixture.Trading.Names.Count(n => n == "cancel_batch"));
    }

    [Fact]
    public async Task KillForgetsAListingErrorOnceLaterLooksAreClean()
    {
        var rest = new FakeAccountRest();
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        var looks = 0;
        fixture.Markets.Listing = () =>
        {
            looks++;
            return looks == 1 ? throw new TimeoutException("listing down") : rest.RestingOrdersAsync(Ct);
        };

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.Equal(3, looks);   // the failed look, then two empty ones a second apart
        Assert.True(report.Clean);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task KillIsNeverCleanWhenItCannotSeeTheOrders()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var looks = 0;
        fixture.Markets.Listing = () =>
        {
            looks++;
            throw new TimeoutException("listing down");
        };

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.Equal(OrderService.KillPasses, looks);   // it keeps trying, then stops
        Assert.False(report.Clean);
        Assert.Empty(report.Cancelled);
        Assert.Equal(OrderService.KillPasses, report.Errors.Count(e => e.Contains("listing orders", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task KillNamesWhatStillRestsWhenTheBatchCancelKeepsFailing()
    {
        var rest = new FakeAccountRest { Orders = [Resting("stuck-1"), Resting("stuck-2")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        fixture.Trading.CancelAllClears = false;
        fixture.Trading.CancelOrdersError = new InvalidOperationException("no route");

        var report = await fixture.Service.KillAsync(cancellationToken: Ct);

        Assert.False(report.Clean);
        Assert.Equal(["stuck-1", "stuck-2"], report.Remaining);
        Assert.Empty(report.Cancelled);
        Assert.Equal(OrderService.KillPasses, report.Errors.Count(e => e.StartsWith("cancel: InvalidOperationException", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheGuardFiresTheKillSwitchOnceTheLossLimitIsReached()
    {
        var rest = new FakeAccountRest { Positions = [AccountData.Position("KXA-1", "0")] };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        Assert.False(await fixture.Service.GuardOnceAsync(Ct));

        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"0","realized_pnl_dollars":"-1.999999"}"""));
        Assert.False(await fixture.Service.GuardOnceAsync(Ct));
        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"0","realized_pnl_dollars":"-2"}"""));
        Assert.True(await fixture.Service.GuardOnceAsync(Ct));

        Assert.True(fixture.State.Halted);
        Assert.Contains("daily loss", fixture.State.HaltReason, StringComparison.Ordinal);
        Assert.False(await fixture.Service.GuardOnceAsync(Ct));   // already halted: not fired again
        Assert.Equal(1, fixture.Trading.Names.Count(n => n == "cancel_all"));
    }

    [Fact]
    public async Task AnOrderAttemptedOverTheLossLimitTripsTheKillSwitch()
    {
        var rest = new FakeAccountRest { Positions = [AccountData.Position("KXA-1", "0")], Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000") };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);
        var prepared = await fixture.Service.PrepareAsync(Buy(), Ct);
        Assert.True(prepared.Verdict.Ok);

        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"0","realized_pnl_dollars":"-2.5"}"""));
        await Assert.ThrowsAsync<OrderRefusedException>(() => fixture.Service.ExecuteAsync(prepared, Ct));

        Assert.True(fixture.State.Halted);
        Assert.Contains("cancel_all", fixture.Trading.Names);
        Assert.DoesNotContain("create", fixture.Trading.Names);
    }

    [Fact]
    public async Task TheBackgroundGuardCanBeStartedAndStopped()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        fixture.Service.StartGuard(TimeSpan.Zero);
        await Task.Delay(30, Ct);
        await fixture.Service.StopGuardAsync();
        await fixture.Service.StopGuardAsync();   // harmless twice
    }

    [Fact]
    public async Task TheBackgroundGuardKillsWhenTheLimitIsReached()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Tracker.Apply(Message("market_position", """{"market_ticker":"KXA-1","position_fp":"0","realized_pnl_dollars":"-5"}"""));
        fixture.Tracker.State.SetBaseline(0);

        fixture.Service.StartGuard(TimeSpan.FromMilliseconds(1));
        await ServiceFixtureWait.UntilAsync(() => fixture.State.Halted);
        await fixture.Service.StopGuardAsync();
    }

    private static WsMessage Message(string type, string json) => new(type, Msg: JsonDocument.Parse(json).RootElement.Clone());

    // --- amend ---------------------------------------------------------------------------------

    private static FakeAccountRest OneResting(string ticker = "KXA-1", string book = "bid", string yesPrice = "0.4000") =>
        new() { Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000"), Orders = [AccountData.Order("o-1", ticker, book, yesPrice)] };

    [Fact]
    public async Task AnAmendIsCheckedAsTheReplacementWithoutCountingTheOldOrder()
    {
        await using var fixture = await ServiceFixture.StartAsync(new RiskPolicy { MaxOpenOrders = 1 }, rest: OneResting());
        var fresh = await fixture.Service.PrepareAsync(Buy(), Ct);
        Assert.Contains(RiskCode.TooManyOrders, fresh.Verdict.Refusals.Select(r => r.Code));

        var prepared = await fixture.Service.PrepareAmendAsync("o-1", priceE6: 450_000, cancellationToken: Ct);

        Assert.True(prepared.Verdict.Ok);   // the one open order is the one being replaced
        Assert.Equal(("o-1", 200L, 200L, 450_000L), (prepared.Replaces, prepared.TotalCountE2, prepared.Intent.CountE2, prepared.Intent.PriceE6));
        var amendment = await fixture.Service.ExecuteAmendAsync(prepared, Ct);
        Assert.Equal(["amend"], fixture.Trading.Names);
        Assert.Equal(("o-1", "KXA-1", BookSide.Bid, 450_000L, 200L, 0), fixture.Trading.Calls[0].Arg);
        Assert.Equal("o-1", amendment.OrderId);
        Assert.Empty(amendment.Transfers);
    }

    [Fact]
    public async Task AnAmendThatWouldBreakALimitIsRefusedAndSendsNothing()
    {
        await using var fixture = await ServiceFixture.StartAsync(rest: OneResting());
        var prepared = await fixture.Service.PrepareAmendAsync("o-1", 900_000, 500, Ct);
        var codes = prepared.Verdict.Refusals.Select(r => r.Code).ToList();
        Assert.True(codes.Contains(RiskCode.OrderTooLarge) || codes.Contains(RiskCode.NotionalTooLarge));

        await Assert.ThrowsAsync<OrderRefusedException>(() => fixture.Service.ExecuteAmendAsync(prepared, Ct));

        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task AmendingABuyOfNoStaysAnAskAndNeverBecomesABid()
    {
        // seen live 2026-10-13: a buy of NO at 1c is held as "sell, no, ask, yes 0.99"; rebuilding it from those labels sent a BID at 0.99, which crosses the book and buys YES
        await using var fixture = await ServiceFixture.StartAsync(rest: OneResting(book: "ask", yesPrice: "0.9900"));

        var prepared = await fixture.Service.PrepareAmendAsync("o-1", priceE6: 980_000, cancellationToken: Ct);

        Assert.Equal((BookSide.Ask, 980_000L, 200L), (prepared.Side, prepared.PriceE6, prepared.TotalCountE2));
        Assert.Equal((TradeOutcome.Yes, TradeAction.Sell), (prepared.Request.Outcome, prepared.Request.Action));
        await fixture.Service.ExecuteAmendAsync(prepared, Ct);
        Assert.Equal(("o-1", "KXA-1", BookSide.Ask, 980_000L, 200L, 0), fixture.Trading.Calls[0].Arg);
    }

    [Fact]
    public async Task AnAmendedOrdersCurrentTotalIsFilledPlusRemaining()
    {
        var rest = OneResting();
        rest.Orders[0] = rest.Orders[0] with { InitialCountFp = Contracts.Parse("1.00"), RemainingCountFp = Contracts.Parse("2.00") };   // amended from 1 to 2: Kalshi leaves the initial count alone
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);

        var prepared = await fixture.Service.PrepareAmendAsync("o-1", priceE6: 450_000, cancellationToken: Ct);

        Assert.Equal((200L, 200L), (prepared.TotalCountE2, prepared.Intent.CountE2));
    }

    [Fact]
    public async Task APartlyFilledOrderMayChangePriceButNotCount()
    {
        var rest = OneResting();
        rest.Orders[0] = rest.Orders[0] with { FillCountFp = Contracts.Parse("1.00"), RemainingCountFp = Contracts.Parse("1.00") };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PrepareAmendAsync("o-1", countE2: 300, cancellationToken: Ct));
        var prepared = await fixture.Service.PrepareAmendAsync("o-1", priceE6: 450_000, cancellationToken: Ct);

        Assert.Contains("partly filled", thrown.Message, StringComparison.Ordinal);
        Assert.Equal((200L, 100L), (prepared.TotalCountE2, prepared.Intent.CountE2));
        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task AnAmendOnAnUnfundedShardFundsItFirst()
    {
        await using var fixture = await ServiceFixture.StartAsync(rest: OneResting("KXT-1"));
        var prepared = await fixture.Service.PrepareAmendAsync("o-1", 450_000, 200, Ct);
        Assert.NotEmpty(prepared.Verdict.Funding);

        await fixture.Service.ExecuteAmendAsync(prepared, Ct);

        Assert.Equal(["transfer", "amend"], fixture.Trading.Names);
    }

    [Fact]
    public async Task BadAmendsAreRefusedBeforeAnythingIsSent()
    {
        var rest = OneResting();
        rest.Orders.Add(AccountData.Order("o-done", "KXA-1", status: "executed"));
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PrepareAmendAsync("o-1", cancellationToken: Ct));
        await Assert.ThrowsAsync<OrderNotFoundException>(() => fixture.Service.PrepareAmendAsync("o-done", 500_000, cancellationToken: Ct));
        var cancel = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PrepareAmendAsync("o-1", countE2: 0, cancellationToken: Ct));
        var plain = await fixture.Service.PrepareAsync(Buy(), Ct);
        var notAmend = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ExecuteAmendAsync(plain, Ct));
        var amend = await fixture.Service.PrepareAmendAsync("o-1", countE2: 100, cancellationToken: Ct);
        var useAmend = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ExecuteAsync(amend, Ct));

        Assert.Contains("cancel", cancel.Message, StringComparison.Ordinal);
        Assert.Contains("not an amend", notAmend.Message, StringComparison.Ordinal);
        Assert.Contains("use ExecuteAmendAsync", useAmend.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Trading.Calls);
    }

    [Fact]
    public async Task AnAmendWithNoClearAnswerIsReportedUnknown()
    {
        await using var fixture = await ServiceFixture.StartAsync(rest: OneResting());
        fixture.Trading.AmendError = new WriteOutcomeUnknownException("amend_order", "o-1");
        var prepared = await fixture.Service.PrepareAmendAsync("o-1", countE2: 100, cancellationToken: Ct);

        await Assert.ThrowsAsync<WriteOutcomeUnknownException>(() => fixture.Service.ExecuteAmendAsync(prepared, Ct));
    }

    // --- cancelling by id, everything, the typed phrase -----------------------------------------

    [Fact]
    public async Task CancelByIdsUsesKalshisRecordAndOneFailureDoesNotStopTheRest()
    {
        var rest = new FakeAccountRest { Orders = [AccountData.Order("o-1", "KXT-1"), AccountData.Order("o-done", "KXA-1", status: "executed"), AccountData.Order("o-2", "KXA-1")] };
        var markets = new FakeMarketRest(rest);
        var trading = new FakeTrading(rest, new TradingState(Path.Combine(Path.GetTempPath(), "kalterm-x-" + Guid.NewGuid().ToString("N") + ".json")));

        var outcomes = await OrderService.CancelByIdsAsync(markets, trading, ["o-1", "o-done", "o-missing", "o-2"], Ct);

        Assert.Equal([("o-1", true), ("o-done", false), ("o-missing", false), ("o-2", true)], outcomes.Select(o => (o.OrderId, o.Ok)));
        Assert.Equal("already executed", outcomes[1].Message);
        Assert.Contains("404", outcomes[2].Message, StringComparison.Ordinal);
        Assert.Equal([new CancelTarget("o-1", 0, "KXT-1"), new CancelTarget("o-2", 0, "KXA-1")], trading.Calls.Select(c => c.Arg));
    }

    [Fact]
    public async Task CancelByIdsReportsAnUnknownOutcomeAndCarriesOn()
    {
        var rest = new FakeAccountRest { Orders = [AccountData.Order("o-1", "KXT-1"), AccountData.Order("o-2", "KXA-1")] };
        var markets = new FakeMarketRest(rest);
        var inner = new FakeTrading(rest, new TradingState(Path.Combine(Path.GetTempPath(), "kalterm-y-" + Guid.NewGuid().ToString("N") + ".json")));

        var outcomes = await OrderService.CancelByIdsAsync(markets, new UnsureTrading(inner), ["o-1", "o-2"], Ct);

        Assert.Equal([("o-1", false), ("o-2", true)], outcomes.Select(o => (o.OrderId, o.Ok)));
        Assert.Equal("no clear answer; look at `kterm orders`", outcomes[0].Message);
    }

    private sealed class UnsureTrading(FakeTrading inner) : ITradingApi
    {
        public Task<CreatedOrder> CreateOrderAsync(OrderRequest order, CancellationToken cancellationToken = default) => inner.CreateOrderAsync(order, cancellationToken);

        public Task<ChangedOrder> AmendOrderAsync(string orderId, string ticker, BookSide side, long priceE6, long countE2, int exchangeIndex, CancellationToken cancellationToken = default) =>
            inner.AmendOrderAsync(orderId, ticker, side, priceE6, countE2, exchangeIndex, cancellationToken);

        public Task<CancelResult> CancelOrderAsync(CancelTarget target, CancellationToken cancellationToken = default) =>
            target.OrderId == "o-1" ? throw new WriteOutcomeUnknownException("cancel_order", "o-1") : inner.CancelOrderAsync(target, cancellationToken);

        public Task<IReadOnlyList<CancelResult>> CancelOrdersAsync(IReadOnlyList<CancelTarget> targets, CancellationToken cancellationToken = default) => inner.CancelOrdersAsync(targets, cancellationToken);

        public Task CancelAllAsync(CancellationToken cancellationToken = default) => inner.CancelAllAsync(cancellationToken);

        public Task<string> TransferAsync(int sourceShard, int destinationShard, long amountE6, string transferId, CancellationToken cancellationToken = default) =>
            inner.TransferAsync(sourceShard, destinationShard, amountE6, transferId, cancellationToken);
    }

    [Fact]
    public async Task CancelEverythingClearsTheOrdersButDoesNotHalt()
    {
        var rest = new FakeAccountRest { Orders = [Resting("o-1"), Resting("o-2")], Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000") };
        await using var fixture = await ServiceFixture.StartAsync(rest: rest);

        var report = await fixture.Service.CancelEverythingAsync(Ct);

        Assert.True(report.Clean);
        Assert.Empty(rest.Orders);
        Assert.Equal(["cancel_all"], fixture.Trading.Names);
        Assert.False(fixture.State.Halted);
        Assert.True((await fixture.Service.PrepareAsync(Buy(), Ct)).Verdict.Ok);   // orders are still allowed afterwards
    }

    [Theory]
    [InlineData("KXA-1 buy yes 2 0.4", true)]
    [InlineData("KXA-1 BUY YES 2.00 0.40", true)]
    [InlineData("  KXA-1   buy  yes  2  0.4  ", true)]
    [InlineData("kxa-1 buy yes 2 0.4", false)]   // the ticker is case-sensitive
    [InlineData("KXA-1 sell yes 2 0.4", false)]
    [InlineData("KXA-1 buy no 2 0.4", false)]
    [InlineData("KXA-1 buy yes 3 0.4", false)]
    [InlineData("KXA-1 buy yes 2 0.41", false)]
    [InlineData("KXA-1 buy yes 2", false)]
    [InlineData("KXA-1 buy yes 2 0.4 now", false)]
    [InlineData("KXA-1 buy yes two 0.4", false)]
    [InlineData("", false)]
    public void TheTypedOrderPhrase(string typed, bool ok)
    {
        Assert.Equal(ok, OrderService.TypedPhraseMatches(typed, Buy()));
    }

    [Fact]
    public async Task DescribeShowsPostOnlyAndTheTimeInForce()
    {
        await using var fixture = await ServiceFixture.StartAsync();

        var prepared = await fixture.Service.PrepareAsync(new TradeRequest("KXA-1", TradeAction.Sell, TradeOutcome.No, 250, 300_000, TimeInForce.ImmediateOrCancel, PostOnly: true), Ct);

        Assert.Equal("SELL 2.5 NO KXA-1 at $0.3000 limit (immediate_or_cancel, post-only), shard 0", OrderService.Describe(prepared)[0]);
    }
}

internal static class ServiceFixtureWait
{
    public static Task UntilAsync(Func<bool> condition) => TrackerFixture.UntilAsync(condition);
}
