using System.Net;
using System.Security.Cryptography;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Session;
using KalTerm.Trading.Transport;
using NSec.Cryptography;

namespace KalTerm.Cli.Tests;

/// <summary>Fills and orders as the account lists them.</summary>
internal sealed class FakeReads(FakeAccountRest rest) : IAccountReads
{
    public List<Fill> Fills { get; } = [];

    public Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken) => rest.BalanceAsync(cancellationToken);

    public IAsyncEnumerable<MarketPosition> PositionsAsync(bool includeSettled, CancellationToken cancellationToken) => rest.PositionsAsync(cancellationToken);

    public IAsyncEnumerable<Order> OrdersAsync(string? status, CancellationToken cancellationToken) =>
        rest.Orders.Where(o => status is null || o.Status == status).ToAsyncEnumerable();

    public IAsyncEnumerable<Fill> FillsAsync(CancellationToken cancellationToken) => Fills.ToAsyncEnumerable();
}

/// <summary>A scripted Kalshi behind the real order service: what the CLI would build from the network, built from fakes.</summary>
internal sealed class TradingRig : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-clitrade-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _open = [];

    public TradingRig(bool production = false, RiskPolicy? policy = null)
    {
        Rest = new FakeAccountRest { Balance = AccountData.Balance("10.3490", "0.0000", "0.0000", "0.0000") };
        Markets = new FakeMarketRest(Rest);
        State = new TradingState(Path.Combine(_folder, "state.json"));
        Trading = new FakeTrading(Rest, State);
        Reads = new FakeReads(Rest);
        Settings = KalshiSettings.Create(production ? KalshiEnvironment.Production : KalshiEnvironment.Demo);
        Policy = policy ?? new RiskPolicy { ProductionEnabled = production };
    }

    public FakeAccountRest Rest { get; }

    public FakeMarketRest Markets { get; }

    public FakeTrading Trading { get; }

    public FakeReads Reads { get; }

    public TradingState State { get; }

    public KalshiSettings Settings { get; }

    public RiskPolicy Policy { get; }

    public List<TradingMode> Modes { get; } = [];

    public Exception? OpenFails { get; set; }

    public async Task<TradingSession> OpenAsync(TradingMode mode, CancellationToken cancellationToken)
    {
        Modes.Add(mode);
        if (OpenFails is not null)
        {
            throw OpenFails;
        }

        var session = new TradingSession(Settings, Policy, State, Reads, Markets);
        if (mode == TradingMode.Read)
        {
            return session;
        }

        var socket = new FakeAccountSocket();
        var tracker = new AccountTracker(Rest, socket, State, new AutoAdvanceTimeProvider(false, AccountData.T0));
        _open.Add(tracker);
        if (mode != TradingMode.Exit)
        {
            await tracker.StartAsync(cancellationToken);
        }

        ITradingApi api = mode == TradingMode.Preview ? new PreviewTrading() : Trading;
        var service = new OrderService(Markets, api, tracker, Policy, Settings.IsProduction, new InstantTimeProvider(), () => "id-" + Interlocked.Increment(ref _ids));
        _open.Add(service);
        session.Service = service;
        session.Trading = api;
        return session;
    }

    private int _ids;

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _open)
        {
            await item.DisposeAsync();
        }

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

public sealed class TradingCommandTests : IAsyncDisposable
{
    private readonly Harness _cli = new();
    private readonly TradingRig _rig = new();

    public TradingCommandTests()
    {
        _cli.OpenTrading = _rig.OpenAsync;
        _cli.TradingState = _rig.State;
    }

    public async ValueTask DisposeAsync()
    {
        _cli.Dispose();
        await _rig.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- placing -----------------------------------------------------------------------------

    [Fact]
    public async Task ADryRunPrintsWhatWouldHappenAndCanSendNothing()
    {
        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--dry-run");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("BUY 2 YES KXA-1 at $0.4000 limit (good_till_canceled), shard 0", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("worst-case cost $0.8", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("dry run: nothing was sent", _cli.Output, StringComparison.Ordinal);
        Assert.Equal([TradingMode.Preview], _rig.Modes);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task ADemoOrderWithYesIsPlacedWithoutAsking()
    {
        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes", "--post-only");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("order o-new placed", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("client id id-1", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(["create"], _rig.Trading.Names);
        var sent = (OrderRequest)_rig.Trading.Calls[0].Arg!;
        Assert.Equal((BookSide.Bid, 400_000L, 200L, true, TimeInForce.GoodTillCanceled), (sent.Side, sent.PriceE6, sent.CountE2, sent.PostOnly, sent.TimeInForce));
        Assert.Contains("environment: demo", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOrderAsksAndSendsOnYes()
    {
        _cli.Interactive = true;
        _cli.Type("y");

        var exit = await _cli.RunAsync("sell", "KXA-1", "1", "0.55", "--ioc");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("Send this order?", _cli.Error, StringComparison.Ordinal);
        var sent = (OrderRequest)_rig.Trading.Calls[0].Arg!;
        Assert.Equal((BookSide.Ask, TimeInForce.ImmediateOrCancel), (sent.Side, sent.TimeInForce));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task AnythingButYesIsNotSent(string answer)
    {
        _cli.Interactive = true;
        _cli.Type(answer);

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("not sent", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task WithNoInputAvailableNothingIsSent()
    {
        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task ANoOrderIsSentAsTheOppositeSideOfTheYesBook()
    {
        await _cli.RunAsync("buy", "KXA-1", "1", "0.30", "--side", "no", "--yes");

        var sent = (OrderRequest)_rig.Trading.Calls[0].Arg!;
        Assert.Equal((BookSide.Ask, 700_000L), (sent.Side, sent.PriceE6));
    }

    [Theory]
    [InlineData("0", "0.4", "count")]
    [InlineData("-1", "0.4", "count")]
    [InlineData("two", "0.4", "count")]
    [InlineData("1.001", "0.4", "count")]
    [InlineData("1", "0", "price")]
    [InlineData("1", "1", "price")]
    [InlineData("1", "cheap", "price")]
    public async Task BadNumbersAreRefusedBeforeAnythingIsOpened(string count, string price, string word)
    {
        var exit = await _cli.RunAsync("buy", "KXA-1", count, price, "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains(word, _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_rig.Modes);
    }

    [Fact]
    public async Task APriceFinerThanKalshiTakesIsRefusedBeforeAnyMoneyMoves()
    {
        var exit = await _cli.RunAsync("buy", "KXT-1", "1", "0.123456", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("decimal places", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task ARefusedOrderPrintsEveryReasonAndSendsNothing()
    {
        var exit = await _cli.RunAsync("buy", "KXA-1", "50", "0.90", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("REFUSED:", _cli.Output, StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task TheVerdictCanBePrintedAsJson()
    {
        var exit = await _cli.RunAsync("buy", "KXT-1", "2", "0.40", "--dry-run", "--json");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("\"approved\":true", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("\"shard\":3", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("\"funding\":[{\"source\":0,\"destination\":3", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("dry run:", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShardWithoutMoneyIsFundedAndSaysSo()
    {
        var exit = await _cli.RunAsync("buy", "KXT-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("first moves $", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("from shard 0 to shard 3", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("moved $", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(["transfer", "create"], _rig.Trading.Names);
    }

    [Fact]
    public async Task AnOrderWithNoClearAnswerExitsTwoNamingTheIdToLookFor()
    {
        _rig.Trading.CreateError = new WriteOutcomeUnknownException("create_order", "id-1");

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes");

        Assert.Equal(2, exit);
        Assert.Contains("UNKNOWN:", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("id-1", _cli.Error, StringComparison.Ordinal);
        Assert.Equal(["id-1"], _rig.State.UnknownOrders);
    }

    [Theory]
    [InlineData(400, "Kalshi said 400")]
    [InlineData(403, "Kalshi said 403")]
    public async Task AKalshiRefusalIsOneClearLine(int status, string expected)
    {
        _rig.Trading.CreateError = new KalshiApiException(status, "{\"error\":\"no\"}");

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains(expected, _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALostConnectionIsOneClearLine()
    {
        _cli.OpenTrading = (_, _) => throw new HttpRequestException("down");

        var exit = await _cli.RunAsync("balance");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("cannot reach Kalshi", _cli.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The server returned status code '401' when status code '101' was expected.", "did not accept the key")]
    [InlineData("The server returned status code '403' when status code '101' was expected.", "did not accept the key")]
    [InlineData("The remote party closed the WebSocket connection without completing the close handshake.", "cannot open Kalshi's private stream")]
    public async Task AStreamThatCannotBeOpenedIsOneClearLineNotAStackTrace(string detail, string expected)
    {
        _rig.OpenFails = new System.Net.WebSockets.WebSocketException(detail);

        var exit = await _cli.RunAsync("buy", "KXA-1", "1", "0.01", "--dry-run");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains(expected, string.Join(' ', _cli.Error.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", _cli.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("config")]
    public async Task AMissingKeyOrBadSettingsIsOneClearLine(string kind)
    {
        _rig.OpenFails = kind == "auth" ? new AuthException("no trading key is stored for demo") : new KalshiConfigurationException("KALSHI_ENV must be demo or production");

        var exit = await _cli.RunAsync("kill");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains(kind == "auth" ? "no trading key" : "KALSHI_ENV", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCostChangingWhileDecidingMeansRunItAgain()
    {
        _cli.Interactive = true;
        _rig.Rest.OnBalanceRead = reads =>
        {
            if (reads > 2)
            {
                _rig.Rest.Balance = AccountData.Balance("0.0000", "10.3490", "0.0000", "0.0000");
            }
        };
        _cli.Type("y");

        var exit = await _cli.RunAsync("buy", "KXT-1", "2", "0.40");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("changed while you were deciding", string.Join(' ', _cli.Error.Split('\n')), StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task ARefusedTransferStopsTheOrder()
    {
        _rig.Trading.TransferError = new KalshiApiException(400, "no");

        var exit = await _cli.RunAsync("buy", "KXT-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("the order was not placed", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransferThatNeverShowsStopsTheOrderWithAdvice()
    {
        _rig.Trading.TransferOverride = (_, _, _, _) => Task.FromResult("ok");

        var exit = await _cli.RunAsync("buy", "KXT-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("kterm balance", _cli.Error, StringComparison.Ordinal);
    }

    // --- production --------------------------------------------------------------------------

    [Fact]
    public async Task AProductionOrderIsOnlySentAfterTheOrderIsTypedOut()
    {
        await using var rig = new TradingRig(production: true);
        _cli.OpenTrading = rig.OpenAsync;
        _cli.Interactive = true;
        _cli.Type("KXA-1 buy yes 2 0.4");

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("PRODUCTION: real money", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("--yes does not skip the production check", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("Type the order to send it", _cli.Error, StringComparison.Ordinal);
        Assert.Equal(["create"], rig.Trading.Names);
    }

    [Theory]
    [InlineData("KXA-1 buy yes 3 0.4")]
    [InlineData("y")]
    [InlineData("yes")]
    public async Task AWrongPhraseInProductionSendsNothing(string typed)
    {
        await using var rig = new TradingRig(production: true);
        _cli.OpenTrading = rig.OpenAsync;
        _cli.Interactive = true;
        _cli.Type(typed);

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("not the order shown above", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Trading.Calls);
    }

    [Fact]
    public async Task InProductionWithNoInputNothingIsSent()
    {
        await using var rig = new TradingRig(production: true);
        _cli.OpenTrading = rig.OpenAsync;

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("no confirmation given", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Trading.Calls);
    }

    [Fact]
    public async Task ProductionWhichTheRiskFileDoesNotAllowIsRefused()
    {
        await using var rig = new TradingRig(production: true, policy: new RiskPolicy());
        _cli.OpenTrading = rig.OpenAsync;
        _cli.Interactive = true;
        _cli.Type("KXA-1 buy yes 2 0.4");

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("REFUSED:", _cli.Output, StringComparison.Ordinal);
        Assert.Empty(rig.Trading.Calls);
    }

    [Fact]
    public async Task ATradingRefusalSaysHowToAllowProduction()
    {
        _rig.Trading.CreateError = new TradingRefusedException("the trading client refuses the production environment");

        var exit = await _cli.RunAsync("buy", "KXA-1", "2", "0.40", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("risk.json", _cli.Error, StringComparison.Ordinal);
    }

    // --- amend -------------------------------------------------------------------------------

    private void OneResting(string ticker = "KXA-1", string book = "bid", string price = "0.4000") =>
        _rig.Rest.Orders = [AccountData.Order("o-1", ticker, book, price)];

    [Fact]
    public async Task AnAmendIsCheckedPrintedConfirmedAndSent()
    {
        OneResting();

        var exit = await _cli.RunAsync("amend", "o-1", "--price", "0.45", "--yes");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("amend order o-1:", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("order o-1 amended", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(("o-1", "KXA-1", BookSide.Bid, 450_000L, 200L, 0), _rig.Trading.Calls[0].Arg);
    }

    [Fact]
    public async Task AnAmendDryRunSendsNothing()
    {
        OneResting();

        var exit = await _cli.RunAsync("amend", "o-1", "--count", "1", "--dry-run");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("dry run: nothing was sent", _cli.Output, StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Theory]
    [InlineData("amend", "o-1")]
    [InlineData("amend", "o-1", "--price", "2")]
    [InlineData("amend", "o-1", "--count", "0")]
    [InlineData("amend", "o-1", "--count", "x")]
    public async Task BadAmendsAreRefusedBeforeAnythingIsSent(params string[] args)
    {
        OneResting();

        var exit = await _cli.RunAsync(args);

        Assert.Equal(CliApp.Failed, exit);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task AnOrderThatIsNotRestingCannotBeAmended()
    {
        _rig.Rest.Orders = [AccountData.Order("o-1", status: "executed")];

        var exit = await _cli.RunAsync("amend", "o-1", "--price", "0.5");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("not resting", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmendThatBreaksALimitIsRefusedWithItsReasons()
    {
        OneResting();

        var exit = await _cli.RunAsync("amend", "o-1", "--price", "0.9", "--count", "50", "--yes");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("REFUSED:", _cli.Output, StringComparison.Ordinal);
        Assert.Empty(_rig.Trading.Calls);
    }

    [Fact]
    public async Task AnAmendCanBePrintedAsJson()
    {
        OneResting();

        await _cli.RunAsync("amend", "o-1", "--price", "0.45", "--dry-run", "--json");

        Assert.Contains("\"approved\":true", _cli.Output, StringComparison.Ordinal);
    }

    // --- getting out -------------------------------------------------------------------------

    [Fact]
    public async Task CancelUsesKalshisRecordAndReportsEachOrder()
    {
        _rig.Rest.Orders = [AccountData.Order("o-1", "KXT-1"), AccountData.Order("o-done", status: "executed")];

        var exit = await _cli.RunAsync("cancel", "o-1", "o-done", "o-missing");

        Assert.Equal(CliApp.Failed, exit);   // not all could be cancelled
        Assert.Contains("o-1: cancelled 1 left", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("o-done: already executed", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("o-missing: Kalshi said 404", _cli.Output, StringComparison.Ordinal);
        Assert.Equal([TradingMode.Exit], _rig.Modes);
    }

    [Fact]
    public async Task CancelOfOneRestingOrderSucceedsAndCanBeJson()
    {
        _rig.Rest.Orders = [AccountData.Order("o-1")];

        var exit = await _cli.RunAsync("cancel", "o-1", "--json");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("\"order_id\":\"o-1\",\"ok\":true", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillHaltsCancelsAndSaysSo()
    {
        _rig.Rest.Orders = [AccountData.Order("o-1"), AccountData.Order("o-2")];

        var exit = await _cli.RunAsync("kill");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("trading is HALTED", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("no orders are resting", _cli.Output, StringComparison.Ordinal);
        Assert.True(_rig.State.Halted);
        Assert.Empty(_rig.Rest.Orders);
    }

    [Fact]
    public async Task KillThatCannotClearTheBookExitsOneAndNamesWhatRests()
    {
        _rig.Rest.Orders = [AccountData.Order("stuck")];
        _rig.Trading.CancelAllClears = false;
        _rig.Trading.CancelNothing = true;

        var exit = await _cli.RunAsync("kill");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("STILL RESTING: stuck", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("problem:", _cli.Error, StringComparison.Ordinal);
        Assert.True(_rig.State.Halted);
    }

    [Fact]
    public async Task KillCanBeJson()
    {
        var exit = await _cli.RunAsync("kill", "--json");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("\"clean\":true", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeClearsTheHaltOnlyAfterAsking()
    {
        _rig.State.Halt("kill switch");
        _cli.Interactive = true;
        _cli.Type("n");
        Assert.Equal(CliApp.Failed, await _cli.RunAsync("resume"));
        Assert.True(_rig.State.Halted);

        _cli.Type("y");
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("resume"));

        Assert.False(_rig.State.Halted);
        Assert.Contains("halt cleared", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeWithYesDoesNotAskAndResumeWhenNotHaltedSaysSo()
    {
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("resume"));
        Assert.Contains("trading is not halted", _cli.Output, StringComparison.Ordinal);

        _rig.State.Halt("daily loss");
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("resume", "--yes"));
        Assert.False(_rig.State.Halted);
    }

    [Fact]
    public async Task ResumeWithADamagedStateFileIsOneClearLine()
    {
        _cli.TradingState = null;
        _cli.Variables["x"] = "y";

        var exit = await _cli.RunAsync("resume");

        Assert.Equal(CliApp.Failed, exit);
    }

    // --- looking -----------------------------------------------------------------------------

    [Fact]
    public async Task BalanceShowsEachShardAndTheTotalAndAHaltBanner()
    {
        _rig.State.Halt("kill switch");

        var exit = await _cli.RunAsync("balance");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("$10.3490", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("total", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("TRADING HALTED (kill switch): kterm resume", _cli.Output, StringComparison.Ordinal);
        Assert.Equal([TradingMode.Read], _rig.Modes);
    }

    [Fact]
    public async Task BalanceAsJson()
    {
        await _cli.RunAsync("balance", "--json");

        Assert.Contains("\"halted\":false", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("\"available\":\"$10.3490\"", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PositionsHideFlatMarketsUnlessAskedAndShowTheSide()
    {
        _rig.Rest.Positions = [AccountData.Position("KXA-1", "3.00", "1.20", "-0.25"), AccountData.Position("KXB-1", "-1.50", "0.40"), AccountData.Position("KXC-1", "0.00")];

        Assert.Equal(CliApp.Ok, await _cli.RunAsync("positions"));

        Assert.Contains("3 YES", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("1.5 NO", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("KXC-1", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("positions", "--all"));
        Assert.Contains("flat", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPositionsSaysSoAndPositionsCanBeJson()
    {
        await _cli.RunAsync("positions");
        Assert.Contains("no open positions", _cli.Output, StringComparison.Ordinal);

        _rig.Rest.Positions = [AccountData.Position("KXA-1", "3.00", "1.20")];
        await _cli.RunAsync("positions", "--json");
        Assert.Contains("\"ticker\":\"KXA-1\"", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdersShowTheYesBookBothPricesAndTheNote()
    {
        _rig.Rest.Orders = [AccountData.Order("o-1", "KXA-1", "bid", "0.4000"), AccountData.Order("o-2", "KXB-1", "ask", "0.9900", status: "executed")];

        Assert.Equal(CliApp.Ok, await _cli.RunAsync("orders"));

        Assert.Contains("o-1", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("o-2", _cli.Output, StringComparison.Ordinal);   // resting is the default
        Assert.Contains("BID", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("$0.6000", _cli.Output, StringComparison.Ordinal);   // the NO price
        Assert.Contains("BID = buy YES", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("orders", "--status", "all"));
        Assert.Contains("o-2", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOrdersSaysSoAndOrdersCanBeJson()
    {
        await _cli.RunAsync("orders");
        Assert.Contains("no orders", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("BID = buy YES", _cli.Output, StringComparison.Ordinal);

        _rig.Rest.Orders = [AccountData.Order("o-1")];
        await _cli.RunAsync("orders", "--json", "--limit", "5");
        Assert.Contains("\"order_id\":\"o-1\"", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FillsShowTheBookSideAndBothPrices()
    {
        _rig.Reads.Fills.Add(new Fill("f-1", "o-1", "KXA-1", "buy", "yes", KalTerm.Core.Money.Contracts.Parse("1.50"), Dollars.Parse("0.40"), Dollars.Parse("0.60"), BookSide: "bid", FeeCost: Dollars.Parse("0.02"), IsTaker: true, CreatedTime: new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero), Ts: 5));

        Assert.Equal(CliApp.Ok, await _cli.RunAsync("fills"));

        Assert.Contains("KXA-1", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("taker", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("$0.0200", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("fills", "--json"));
        Assert.Contains("\"fill_id\":\"f-1\"", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFillsSaysSo()
    {
        await _cli.RunAsync("fills");

        Assert.Contains("no fills", _cli.Output, StringComparison.Ordinal);
    }

    // --- the trading key ---------------------------------------------------------------------

    private sealed class MemoryVault : ITradingKeyVault
    {
        public Dictionary<string, TradingKeyEntry> Entries { get; } = [];

        public void Save(TradingKeyEntry entry) => Entries[entry.Environment] = entry;

        public TradingKeyEntry? Load(string environment) => Entries.GetValueOrDefault(environment);

        public bool Delete(string environment) => Entries.Remove(environment);
    }

    private static string NewPem()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new string(PemEncoding.Write("PRIVATE KEY", key.Export(KeyBlobFormat.PkixPrivateKey)));
    }

    [Fact]
    public async Task TheTradingKeyIsStoredShownAndDeletedWithoutEverBeingPrinted()
    {
        var vault = new MemoryVault();
        _cli.Vault = vault;
        var pem = NewPem();
        var file = Path.Combine(Path.GetTempPath(), "kalterm-pem-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(file, pem, Ct);
        try
        {
            Assert.Equal(CliApp.Ok, await _cli.RunAsync("trading-key", "set", "--env", "demo", "--key-id", "abcd-1234-wxyz", "--pem-file", file));
            Assert.Equal("abcd-1234-wxyz", vault.Entries["demo"].KeyId);
            Assert.Contains("id ends …wxyz", _cli.Output, StringComparison.Ordinal);

            Assert.Equal(CliApp.Ok, await _cli.RunAsync("trading-key", "show"));
            Assert.Contains("demo: stored (id ends …wxyz)", _cli.Output, StringComparison.Ordinal);
            Assert.Contains("production: none", _cli.Output, StringComparison.Ordinal);

            Assert.Equal(CliApp.Ok, await _cli.RunAsync("trading-key", "delete", "--env", "demo"));
            Assert.Contains("deleted the demo trading key", _cli.Output, StringComparison.Ordinal);
            Assert.Equal(CliApp.Ok, await _cli.RunAsync("trading-key", "delete", "--env", "demo"));
            Assert.Contains("there was no demo trading key", _cli.Output, StringComparison.Ordinal);

            Assert.DoesNotContain("PRIVATE", _cli.Output + _cli.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("abcd-1234", _cli.Output + _cli.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ABadKeyFileOrIdIsRefusedAndNothingIsStored()
    {
        var vault = new MemoryVault();
        _cli.Vault = vault;
        var file = Path.Combine(Path.GetTempPath(), "kalterm-pem-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(file, "not a key", Ct);
        try
        {
            Assert.Equal(CliApp.Failed, await _cli.RunAsync("trading-key", "set", "--key-id", "kid", "--pem-file", file));
            Assert.Equal(CliApp.Failed, await _cli.RunAsync("trading-key", "set", "--key-id", "kid", "--pem-file", file + ".missing"));
            Assert.Empty(vault.Entries);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
