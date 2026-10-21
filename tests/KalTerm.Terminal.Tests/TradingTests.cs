using System.Net;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Rest;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Trading;
using KalTerm.Terminal.Views;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Transport;

namespace KalTerm.Terminal.Tests;

public sealed class TradingProblemsTests
{
    public static TheoryData<Exception, string> Sentences => new()
    {
        { new PlanChangedException(), "Check the order again." },
        { new TransferFailedException("the move failed"), "the move failed; the order was not placed." },
        { new WriteOutcomeUnknownException("order", "c-77"), "Look for c-77 in Orders and Fills" },
        { new TradingRefusedException("production writes are off"), "\"ProductionEnabled\": true in risk.json" },
        { new KalshiApiException(429, "slow down"), "Kalshi said 429: slow down" },
        { new HttpRequestException("no route"), "Cannot reach Kalshi: HttpRequestException." },
        { new WebSocketException("the server returned 401"), "did not accept the key" },
        { new WebSocketException("closed"), "Cannot open Kalshi's private stream: closed" },
        { new TimeoutException(), "did not answer in time" },
        { new AuthException("no trading key is stored for production"), "no trading key is stored for production" },
    };

    [Theory]
    [MemberData(nameof(Sentences))]
    public void EveryFailureTradingCanEndInIsAnsweredInWords(Exception ex, string expected)
    {
        Assert.True(TradingProblems.IsExpected(ex));
        Assert.Contains(expected, TradingProblems.Say(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclearOutcomeIsToldApartFromTheRestAndAnUnexpectedFailureIsNotHidden()
    {
        Assert.True(TradingProblems.IsUnknown(new WriteOutcomeUnknownException("order", "c-1")));
        Assert.True(TradingProblems.IsUnknown(new TransferUnknownException("moved?")));
        Assert.False(TradingProblems.IsUnknown(new PlanChangedException()));
        Assert.False(TradingProblems.IsExpected(new InvalidOperationException("a bug")));
        Assert.StartsWith("UNKNOWN:", TradingProblems.Say(new WriteOutcomeUnknownException("order", "c-1")), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => TradingProblems.Say(null!));
    }

    [Fact]
    public void ABodyOfKalshisIsCutAtThreeHundredCharacters()
    {
        var said = TradingProblems.Say(new KalshiApiException(500, new string('x', 900)));

        Assert.True(said.Length < 350);
    }

    [Fact]
    public void MoneyAndCountsAreWrittenFromTheExactIntegers()
    {
        Assert.Equal("$0.4000", TradingFormat.Dollars(400_000));
        Assert.Equal("0.4000", TradingFormat.Plain(400_000));
        Assert.Equal("$0.123456", TradingFormat.Dollars(123_456));
        Assert.Equal("-$1.5000", TradingFormat.Dollars(-1_500_000));
        Assert.Equal("2", TradingFormat.Count(200));
        Assert.Equal("1.5", TradingFormat.Count(150));
    }
}

public sealed class OrderTicketViewModelTests : IAsyncDisposable
{
    private readonly FakeTradingLink _link = new();

    public ValueTask DisposeAsync() => _link.DisposeAsync();

    private OrderTicketViewModel New(bool production = true, string ticker = "KX-A", string? price = null) => OrderTicketViewModel.ForNewOrder(_link, production, ticker, price);

    private static async Task FillAndCheckAsync(OrderTicketViewModel ticket)
    {
        ticket.CountText = "2";
        ticket.PriceText = "0.40";
        await ticket.CheckCommand.RunAsync();
    }

    [Fact]
    public void ANewTicketStartsWithWhatItWasGivenAndSaysNothingWhileItIsEmpty()
    {
        var ticket = New(price: "0.57");

        Assert.Equal(("New order", "KX-A", "0.57", true, true), (ticket.Title, ticket.Ticker, ticket.PriceText, ticket.IsNew, ticket.RequiresTyping));
        Assert.False(ticket.IsAmend);
        Assert.False(New(false).RequiresTyping);
        Assert.Equal(("Buy", "Yes"), (ticket.Action, ticket.Outcome));
        Assert.False(ticket.HasProblem);
        Assert.False(ticket.CanCheck);
        Assert.Throws<ArgumentNullException>(() => OrderTicketViewModel.ForNewOrder(null!, true, "KX-A", null));
        Assert.Throws<ArgumentNullException>(() => OrderTicketViewModel.ForNewOrder(_link, true, null!, null));
    }

    [Theory]
    [InlineData("", "2", "0.4", "Type the market's ticker.")]
    [InlineData("KX-A", "0", "0.4", "The count is a positive number of contracts with at most two decimals.")]
    [InlineData("KX-A", "abc", "0.4", "The count is a positive number of contracts with at most two decimals.")]
    [InlineData("KX-A", "2.555", "0.4", "The count is a positive number of contracts with at most two decimals.")]
    [InlineData("KX-A", "2", "1", "The price is dollars strictly between 0 and 1, for example 0.40.")]
    [InlineData("KX-A", "2", "0", "The price is dollars strictly between 0 and 1, for example 0.40.")]
    [InlineData("KX-A", "2", "0.1234567", "The price is dollars strictly between 0 and 1, for example 0.40.")]
    public void WhatCannotBeAnOrderIsSaidInWordsAndCannotBeChecked(string ticker, string count, string price, string problem)
    {
        var ticket = New(ticker: ticker);

        ticket.CountText = count;
        ticket.PriceText = price;

        Assert.Equal((problem, true, false), (ticket.Problem, ticket.HasProblem, ticket.CanCheck));
    }

    [Fact]
    public async Task CheckingAsksTheRiskLayerWithTheExactUnitsAndShowsWhatWouldHappen()
    {
        var ticket = New();
        ticket.Action = "Sell";
        ticket.Outcome = "No";
        ticket.ImmediateOrCancel = true;
        ticket.PostOnly = true;
        ticket.CountText = " 1.5 ";
        ticket.PriceText = "0.123456";

        await ticket.CheckCommand.RunAsync();

        Assert.Equal(new OrderEntry("KX-A", false, false, 150, 123_456, true, true), Assert.Single(_link.Prepared));
        Assert.Equal(_link.NextPlan.Lines, ticket.PlanLines);
        Assert.True(ticket.PlanOk);
        Assert.True(ticket.HasPlan);
        Assert.False(ticket.Busy);
        Assert.Empty(_link.Sent);
    }

    [Fact]
    public async Task NothingCanBeSentBeforeACheckApprovedItAndInProductionTheOrderIsTyped()
    {
        var ticket = New();
        ticket.CountText = "2";
        ticket.PriceText = "0.40";
        Assert.False(ticket.CanSend);
        Assert.False(ticket.ShowTyping);

        await ticket.CheckCommand.RunAsync();
        Assert.True(ticket.ShowTyping);
        Assert.False(ticket.CanSend);   // approved, but not confirmed

        ticket.TypedText = "KX-A buy yes 2 0.41";
        Assert.False(ticket.PhraseOk);
        Assert.False(ticket.CanSend);
        ticket.TypedText = "kx-a BUY yes 2 0.40";
        Assert.True(ticket.PhraseOk);
        Assert.True(ticket.CanSend);
    }

    [Fact]
    public async Task InDemoTheCheckAloneIsEnoughToSend()
    {
        var ticket = New(production: false);

        await FillAndCheckAsync(ticket);

        Assert.True(ticket.CanSend);
        Assert.False(ticket.ShowTyping);
    }

    [Fact]
    public async Task ARefusedOrderCanNeverBeSentWhateverIsTyped()
    {
        _link.NextPlan = TradingSamples.Plan(ok: false);
        var ticket = New();

        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;

        Assert.False(ticket.PlanOk);
        Assert.False(ticket.CanSend);
        Assert.Contains("REFUSED: the order is bigger than the limit", ticket.PlanLines);
        await ticket.SendCommand.RunAsync();
        Assert.Empty(_link.Sent);
    }

    [Fact]
    public async Task ChangingAnythingThrowsTheCheckAndTheConfirmationAway()
    {
        var ticket = New();
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;
        Assert.True(ticket.CanSend);

        ticket.PriceText = "0.41";

        Assert.Equal((false, false, string.Empty, false), (ticket.HasPlan, ticket.CanSend, ticket.TypedText, ticket.ShowTyping));
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;
        ticket.PostOnly = true;
        Assert.False(ticket.HasPlan);
        ticket.PostOnly = true;   // unchanged: nothing happens
        ticket.ImmediateOrCancel = false;
    }

    [Fact]
    public async Task SendingPlacesTheOrderShowsWhatHappenedAndEndsTheTicket()
    {
        var closed = 0;
        var ticket = New();
        ticket.Closed += () => closed++;
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;

        await ticket.SendCommand.RunAsync();

        Assert.Equal(_link.SendLines, ticket.Result);
        Assert.True(ticket.Sent);
        Assert.True(ticket.HasResult);
        Assert.False(ticket.CanSend);
        Assert.False(ticket.CanCheck);
        Assert.Single(_link.Sent);
        ticket.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task APlanThatChangedWhileDecidingMustBeCheckedAgainAndNothingWasSent()
    {
        var ticket = New();
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;
        _link.SendFails = new PlanChangedException();

        await ticket.SendCommand.RunAsync();

        Assert.Contains("Check the order again.", ticket.Error, StringComparison.Ordinal);
        Assert.Equal((false, false, false), (ticket.HasPlan, ticket.Sent, ticket.Unknown));
        Assert.True(ticket.CanCheck);
    }

    [Fact]
    public async Task AnUnclearAnswerStopsTheTicketFromSendingAgain()
    {
        var ticket = New();
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;
        _link.SendFails = new WriteOutcomeUnknownException("order", "c-9");

        await ticket.SendCommand.RunAsync();

        Assert.StartsWith("UNKNOWN:", ticket.Error, StringComparison.Ordinal);
        Assert.Contains("c-9", ticket.Error, StringComparison.Ordinal);
        Assert.True(ticket.Unknown);
        Assert.False(ticket.CanSend);
        Assert.False(ticket.CanCheck);
        Assert.False(ticket.ShowTyping);
        await ticket.SendCommand.RunAsync();
        Assert.Single(_link.Sent);
    }

    [Fact]
    public async Task ARefusalFromTheExchangeIsShownAndTheCheckIsDropped()
    {
        var ticket = New();
        await FillAndCheckAsync(ticket);
        ticket.TypedText = _link.Phrase;
        _link.SendFails = new OrderRefusedException("the market is closed");

        await ticket.SendCommand.RunAsync();

        Assert.Equal("the market is closed", ticket.Error);
        Assert.False(ticket.HasPlan);
    }

    [Fact]
    public async Task AFailedCheckIsShownInWords()
    {
        var ticket = New();
        _link.PrepareFails = new KalshiApiException(404, "no such market");

        await FillAndCheckAsync(ticket);

        Assert.StartsWith("Kalshi said 404", ticket.Error, StringComparison.Ordinal);
        Assert.False(ticket.HasPlan);
        Assert.False(ticket.Busy);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsShownAsItIsNotSwallowedAsATradingProblem()
    {
        var ticket = New();
        _link.PrepareFails = new InvalidOperationException("something odd");

        await FillAndCheckAsync(ticket);

        Assert.Equal("something odd", ticket.Error);
    }

    [Fact]
    public void AChangeStartsFromTheOrderAndNeedsANewPriceOrCount()
    {
        var ticket = OrderTicketViewModel.ForAmend(_link, true, TradingSamples.Order(price: 400_000, initial: 250));

        Assert.Equal(("Change order", true, false, "0.4000", "2.5", "KX-A"), (ticket.Title, ticket.IsAmend, ticket.IsNew, ticket.PriceText, ticket.CountText, ticket.Ticker));
        Assert.Contains("bid 2 of 2.5 resting at $0.4000", ticket.AmendingText, StringComparison.Ordinal);
        Assert.Null(ticket.Problem);

        ticket.PriceText = string.Empty;
        ticket.CountText = string.Empty;
        Assert.Equal("Give a new price or a new total count.", ticket.Problem);
        ticket.CountText = "abc";
        Assert.Equal("The count is a positive number of contracts with at most two decimals.", ticket.Problem);
        ticket.CountText = string.Empty;
        ticket.PriceText = "2";
        Assert.Equal("The price is dollars strictly between 0 and 1, for example 0.40.", ticket.Problem);
        Assert.Throws<ArgumentNullException>(() => OrderTicketViewModel.ForAmend(null!, true, TradingSamples.Order()));
        Assert.Throws<ArgumentNullException>(() => OrderTicketViewModel.ForAmend(_link, true, null!));
    }

    [Fact]
    public async Task ChangingAnOrderChecksTheNewPriceAndCountAndSendsAnAmendment()
    {
        var ticket = OrderTicketViewModel.ForAmend(_link, false, TradingSamples.Order("ord-5"));
        ticket.PriceText = "0.50";
        ticket.CountText = string.Empty;

        await ticket.CheckCommand.RunAsync();
        await ticket.SendCommand.RunAsync();

        Assert.Equal(("ord-5", 500_000L, (long?)null), Assert.Single(_link.AmendPrepared));
        Assert.True(Assert.Single(_link.Sent).IsAmend);
        Assert.True(ticket.Sent);
    }

    [Fact]
    public async Task ChangingJustTheCountLeavesThePriceAlone()
    {
        var ticket = OrderTicketViewModel.ForAmend(_link, false, TradingSamples.Order("ord-5"));
        ticket.PriceText = string.Empty;
        ticket.CountText = "3";

        await ticket.CheckCommand.RunAsync();

        Assert.Equal(("ord-5", (long?)null, (long?)300), Assert.Single(_link.AmendPrepared));
    }
}

public sealed class TradingViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeTradingGateway _gateway = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly TradingViewModel _model;

    public TradingViewModelTests() => _model = new TradingViewModel(_gateway, _format, _dialogs, _t.Time, InlineContext.Instance);

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private FakeTradingLink Link => _gateway.Link;

    private async Task ConnectAsync(AccountView? account = null)
    {
        if (account is not null)
        {
            Link.Account = account;
        }

        await _model.ConnectAsync();
    }

    [Fact]
    public async Task ConnectingOpensTheSessionAndShowsTheAccount()
    {
        Assert.Equal((LinkState.NotConnected, false), (_model.State, _model.IsConnected));
        Assert.True(_model.ConnectCommand.CanExecute(null));
        Assert.False(_model.NewOrderCommand.CanExecute(null));

        await ConnectAsync(TradingSamples.Account(orders: [TradingSamples.Order()], positions: [new PositionView("KX-A", 250, 600_000, -100_000), new PositionView("KX-B", -150, 0, 0)], fills: [new FillView("t1", new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero), "KX-A", "buy", "yes", 400_000, 200, 10_000, "ord-1")]));

        Assert.Equal((LinkState.Connected, "Connected to Kalshi."), (_model.State, _model.StateText));
        Assert.Equal("PRODUCTION: real money on your Kalshi account", _model.EnvironmentText);
        Assert.True(_model.Production);
        Assert.Equal("shard 0: $1.2000   shard 1: $0.3000   total: $1.5000", _model.BalanceText);
        Assert.Equal(("$0.5000 of $3.0000", "1 of 3", "$0.0000 made today (trading stops at $2.0000 lost)"), (_model.ExposureText, _model.OpenOrdersText, _model.DayText));
        Assert.Contains("at most 5 contracts and $1.0000", _model.LimitsText, StringComparison.Ordinal);
        var order = Assert.Single(_model.Orders);
        Assert.Equal(("KX-A", "bid (buys YES)", "$0.4000", "2", "0", "0"), (order.Ticker, order.Side, order.Price, order.Remaining, order.Filled, order.Shard));
        Assert.Equal(["2.5 YES", "1.5 NO"], _model.Positions.Select(p => p.Position));
        Assert.Equal("$0.6000", _model.Positions[0].Exposure);
        Assert.Equal("-$0.1000", _model.Positions[0].Realized);
        var fill = Assert.Single(_model.Fills);
        Assert.Equal(("2026-10-20 20:00:00", "KX-A", "buy yes", "$0.4000", "2", "$0.0100"), (fill.Time, fill.Ticker, fill.What, fill.Price, fill.Count, fill.Fee));
        Assert.False(_model.ConnectCommand.CanExecute(null));
        Assert.True(_model.NewOrderCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDemoAccountIsSaidToBeDemoAndALossIsShownAsAMeterAgainstItsLimit()
    {
        await ConnectAsync(TradingSamples.Account(production: false, realized: -750_000, shards: []));

        Assert.Equal(("DEMO: no real money", false, "reading the balance…"), (_model.EnvironmentText, _model.Production, _model.BalanceText));
        Assert.Equal("$0.7500 lost today of $2.0000", _model.DayText);
    }

    [Fact]
    public async Task AFailureToConnectIsShownAndConnectTriesAgain()
    {
        _gateway.OpenFails = new AuthException("no trading key is stored for production");

        await _model.ConnectAsync();

        Assert.Equal((LinkState.Failed, "no trading key is stored for production"), (_model.State, _model.StateText));
        Assert.True(_model.ConnectCommand.CanExecute(null));
        _gateway.OpenFails = null;

        await _model.ConnectCommand.RunAsync();

        Assert.Equal(LinkState.Connected, _model.State);
        Assert.Equal(2, _gateway.Opens);
    }

    [Fact]
    public async Task ASessionThatDoesNotOpenInTimeIsGivenUp()
    {
        _gateway.OpenHold = new TaskCompletionSource();

        var connecting = _model.ConnectAsync();
        Assert.Equal(LinkState.Connecting, _model.State);
        Assert.False(_model.ConnectCommand.CanExecute(null));
        await Wait.FireUntilAsync(_t.Time, () => connecting.IsCompleted);

        Assert.Equal((LinkState.Failed, "Kalshi did not answer in time. Press Connect to try again."), (_model.State, _model.StateText));
    }

    [Fact]
    public async Task ConnectingTwiceOpensOnce()
    {
        await _model.ConnectAsync();
        await _model.ConnectAsync();

        Assert.Equal(1, _gateway.Opens);
    }

    [Fact]
    public async Task WhateverChangesInTheAccountIsShownWithoutAskingForIt()
    {
        await ConnectAsync();
        Assert.Empty(_model.Orders);

        Link.Account = TradingSamples.Account(orders: [TradingSamples.Order("a"), TradingSamples.Order("b", price: 450_000)], synced: false, fault: "the stream dropped");
        Link.RaiseChanged();

        Assert.Equal(["$0.4000", "$0.4500"], _model.Orders.Select(o => o.Price));
        Assert.Equal("The live account view is not in sync (the stream dropped): new orders are refused until it is.", _model.SyncText);
        Assert.True(_model.HasSyncProblem);
        Link.Account = TradingSamples.Account(synced: true);
        Link.RaiseChanged();
        Assert.False(_model.HasSyncProblem);
    }

    [Fact]
    public async Task TheTimesOfFillsFollowTheZone()
    {
        await ConnectAsync(TradingSamples.Account(fills: [new FillView("t1", new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero), "KX-A", "buy", "yes", 400_000, 200, 0, "o"), new FillView("t2", null, "KX-B", "sell", "no", 400_000, 100, 0, "o")]));
        Assert.Equal(["2026-10-20 20:00:00", "-"], _model.Fills.Select(f => f.Time));

        _format.Zone = TimeZoneInfo.CreateCustomTimeZone("z", TimeSpan.FromHours(2), "z", "z");

        Assert.Equal("2026-10-20 22:00:00", _model.Fills[0].Time);
    }

    [Fact]
    public async Task AHaltedAccountSaysSoAndResumeAsksFirst()
    {
        await ConnectAsync(TradingSamples.Account(halted: true, haltReason: "kill switch (terminal)"));
        Assert.Equal("HALTED: new orders are refused (kill switch (terminal)). Resume allows them again.", _model.HaltText);
        Assert.True(_model.ResumeCommand.CanExecute(null));
        _dialogs.Confirm = false;

        await _model.ResumeCommand.RunAsync();
        Assert.Equal(0, Link.Resumes);
        _dialogs.Confirm = true;
        await _model.ResumeCommand.RunAsync();

        Assert.Equal(1, Link.Resumes);
        Assert.False(_model.Halted);
        Assert.Equal(string.Empty, _model.HaltText);
        Assert.Equal("The halt is cleared; new orders are allowed again.", _model.LastAction);
        Assert.False(_model.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AHaltWithNoReasonIsCalledTheKillSwitch()
    {
        await ConnectAsync(TradingSamples.Account(halted: true));

        Assert.Contains("(kill switch)", _model.HaltText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKillSwitchAsksFirstThenCancelsEverythingAndReportsWhatItDid()
    {
        await ConnectAsync();
        _dialogs.Confirm = false;
        await _model.KillCommand.RunAsync();
        Assert.Equal(0, Link.Kills);

        _dialogs.Confirm = true;
        await _model.KillCommand.RunAsync();

        Assert.Equal(1, Link.Kills);
        Assert.Equal(["Trading is HALTED (new orders are refused until you press Resume).", "Cancelled 2 order(s) in the follow-up passes.", "No orders are resting."], _model.LastAction.Split(Environment.NewLine));
        Assert.Contains("Cancel every open order", _dialogs.Asked[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKillThatLeavesOrdersRestingOrFailsSaysSoPlainly()
    {
        await ConnectAsync();
        Link.KillAnswer = new KillResult(1, ["ord-1", "ord-2"], ["a 503 from Kalshi"]);

        await _model.KillCommand.RunAsync();

        Assert.Contains("STILL RESTING: ord-1, ord-2", _model.LastAction, StringComparison.Ordinal);
        Assert.Contains("Problem: a 503 from Kalshi", _model.LastAction, StringComparison.Ordinal);
        Assert.DoesNotContain("No orders are resting.", _model.LastAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKillSwitchWorksWithoutASessionByOpeningTheLightestOne()
    {
        _gateway.OpenFails = new AuthException("no trading key");
        await _model.ConnectAsync();
        Assert.True(_model.KillCommand.CanExecute(null));

        await _model.KillCommand.RunAsync();

        Assert.Equal((1, 0), (_gateway.Kills, Link.Kills));
        Assert.Contains("Cancelled 1 order(s)", _model.LastAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKillThatCannotReachKalshiIsShown()
    {
        var failing = new FailingKillGateway();
        using var model = new TradingViewModel(failing, _format, _dialogs, _t.Time, InlineContext.Instance);

        await model.KillCommand.RunAsync();

        Assert.Equal("Cannot reach Kalshi: HttpRequestException.", model.Error);
    }

    [Fact]
    public async Task TheKillSwitchCannotBeStartedWhileConnecting()
    {
        _gateway.OpenHold = new TaskCompletionSource();

        var connecting = _model.ConnectAsync();

        Assert.False(_model.KillCommand.CanExecute(null));
        await Wait.FireUntilAsync(_t.Time, () => connecting.IsCompleted);
        Assert.True(_model.KillCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingAnOrderAsksFirstAndSaysWhatHappened()
    {
        await ConnectAsync(TradingSamples.Account(orders: [TradingSamples.Order("ord-1")]));
        _dialogs.Confirm = false;
        await _model.Orders[0].CancelCommand.RunAsync();
        Assert.Empty(Link.Cancelled);

        _dialogs.Confirm = true;
        await _model.Orders[0].CancelCommand.RunAsync();

        Assert.Equal(["ord-1"], Link.Cancelled);
        Assert.Equal("order ord-1 cancelled", _model.LastAction);
        Assert.Contains("Cancel the resting order on KX-A (bid 2 at $0.4000)?", _dialogs.Asked[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCancelIsShown()
    {
        await ConnectAsync(TradingSamples.Account(orders: [TradingSamples.Order("ord-1", side: "ask")]));
        Link.CancelFails = new KalshiApiException(503, "busy");

        await _model.Orders[0].CancelCommand.RunAsync();

        Assert.StartsWith("Kalshi said 503", _model.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewOrderOpensTheTicketAndWhatItDidIsShownAfterwards()
    {
        await ConnectAsync();
        _dialogs.OnTicket = async ticket =>
        {
            ticket.CountText = "2";
            ticket.PriceText = "0.40";
            await ticket.CheckCommand.RunAsync();
            ticket.TypedText = Link.Phrase;
            await ticket.SendCommand.RunAsync();
        };

        await _model.OpenTicketAsync("KX-A", "0.57");

        var ticket = Assert.Single(_dialogs.Tickets);
        Assert.Equal(("KX-A", true), (ticket.Ticker, ticket.RequiresTyping));
        Assert.Equal(Link.SendLines, _model.LastAction.Split(Environment.NewLine));
    }

    [Fact]
    public async Task TheNewOrderButtonOpensAnEmptyTicketAndATicketNeedsASession()
    {
        await _model.OpenTicketAsync("KX-A", null);
        Assert.Empty(_dialogs.Tickets);

        await ConnectAsync();
        await _model.NewOrderCommand.RunAsync();

        Assert.Equal(string.Empty, Assert.Single(_dialogs.Tickets).Ticker);
    }

    [Fact]
    public async Task AnUnclearOrderIsShownOnThePageAfterTheTicketCloses()
    {
        await ConnectAsync();
        Link.SendFails = new WriteOutcomeUnknownException("order", "c-5");
        _dialogs.OnTicket = async ticket =>
        {
            ticket.CountText = "2";
            ticket.PriceText = "0.40";
            await ticket.CheckCommand.RunAsync();
            ticket.TypedText = Link.Phrase;
            await ticket.SendCommand.RunAsync();
        };

        await _model.OpenTicketAsync("KX-A", null);

        Assert.StartsWith("UNKNOWN:", _model.LastAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAnOrderOpensAChangeTicketForIt()
    {
        await ConnectAsync(TradingSamples.Account(orders: [TradingSamples.Order("ord-1", price: 400_000)]));
        _dialogs.OnTicket = async ticket =>
        {
            ticket.PriceText = "0.50";
            ticket.CountText = string.Empty;
            await ticket.CheckCommand.RunAsync();
            ticket.TypedText = Link.Phrase;
            await ticket.SendCommand.RunAsync();
        };

        await _model.Orders[0].AmendCommand.RunAsync();

        Assert.Equal(("ord-1", 500_000L, (long?)null), Assert.Single(Link.AmendPrepared));
        Assert.True(Assert.Single(Link.Sent).IsAmend);
        Assert.True(Assert.Single(_dialogs.Tickets).IsAmend);
    }

    [Fact]
    public async Task LettingGoOfThePageLetsGoOfTheSession()
    {
        await ConnectAsync();

        _model.Dispose();
        Link.RaiseChanged();

        Assert.True(Link.Disposed);
        _model.Dispose();
    }

    [Fact]
    public async Task AnErrorInAnActionIsShownOnThePage()
    {
        await ConnectAsync(TradingSamples.Account(orders: [TradingSamples.Order()]));
        _dialogs.OnTicket = _ => throw new InvalidOperationException("the ticket broke");

        await _model.Orders[0].AmendCommand.RunAsync();
        Assert.Equal("the ticket broke", _model.Error);
        Assert.True(_model.HasError);
        await _model.NewOrderCommand.RunAsync();
    }

    private sealed class FailingKillGateway : ITradingGateway
    {
        public Task<ITradingLink> OpenAsync(CancellationToken cancellationToken) => Task.FromException<ITradingLink>(new HttpRequestException("no route"));

        public Task<KillResult> KillAsync(CancellationToken cancellationToken) => Task.FromException<KillResult>(new HttpRequestException("no route"));
    }
}

public sealed class TradingWiringTests : IDisposable
{
    private static readonly Uri Server = new("https://kaldb:5280");

    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toaster = new();
    private readonly FakeTradingGateway _gateway = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly StatusViewModel _status;
    private readonly MarketsViewModel _markets;
    private readonly AlertsViewModel _alerts;
    private readonly AlertNotifier _notifier;
    private readonly TradingViewModel _trading;
    private readonly MainViewModel _model;

    public TradingWiringTests()
    {
        _status = new StatusViewModel(_format, _t.Time);
        _markets = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        _alerts = new AlertsViewModel(_format, _dialogs);
        _notifier = new AlertNotifier(_toaster, _format, _t.Settings, _t.Time, () => _alerts.Silent, InlineContext.Instance);
        _trading = new TradingViewModel(_gateway, _format, _dialogs, _t.Time, InlineContext.Instance);
        _model = new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, _notifier, _toaster, _trading);
        _t.Settings.SaveServer(Server);
        _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
    }

    public void Dispose()
    {
        _model.Layouts.Dispose();
        _trading.Dispose();
        _notifier.Dispose();
        _alerts.Dispose();
        _status.Dispose();
        _markets.Dispose();
        _t.Dispose();
    }

    [Fact]
    public async Task TheSessionWithKalshiOpensByItselfWhenTheTerminalStarts()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _trading.State == LinkState.Connected);

        Assert.Equal(1, _gateway.Opens);
        Assert.Equal("Trading", _model.Navigation[3].Title);
    }

    [Fact]
    public async Task AFailureToOpenItNeverStopsTheRestOfTheTerminal()
    {
        _gateway.OpenFails = new AuthException("no trading key is stored for production");

        await _model.StartAsync();
        await Wait.UntilAsync(() => _trading.State == LinkState.Failed);

        Assert.Equal(ConnectionState.Connected, _model.State);
        Assert.Equal("no trading key is stored for production", _trading.StateText);
    }

    [Fact]
    public async Task TradeOnAnOpenMarketShowsTheTradingPageWithATicketForIt()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _trading.State == LinkState.Connected);
        await _markets.OpenAsync(_markets.Rows[0]);

        _markets.Detail!.TradeCommand.Execute(null);
        await Wait.UntilAsync(() => _dialogs.Tickets.Count == 1);

        Assert.Same(_trading, _model.CurrentPage);
        Assert.Equal("KX-A", _dialogs.Tickets[0].Ticker);
        Assert.Null(_markets.Detail.SuggestedPrice);   // no tick has come yet
    }
}

public sealed class TradingViewTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);

    public void Dispose() => _t.Dispose();

    [Fact]
    public void TheTradingPageShowsTheAccountTheOrdersAndTheKillSwitchWithoutBindingErrors()
    {
        var (texts, buttons) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var gateway = new FakeTradingGateway();
            gateway.Link.Account = TradingSamples.Account(orders: [TradingSamples.Order()], positions: [new PositionView("KX-A", 250, 600_000, 0)], halted: true, haltReason: "kill switch");
            using var model = new TradingViewModel(gateway, _format, _dialogs, _t.Time, InlineContext.Instance);
            model.ConnectAsync().GetAwaiter().GetResult();
            var view = new TradingView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return (VisualTreeKit.Texts(view).ToList(), VisualTreeKit.All<Button>(view).Select(b => b.Content as string).ToList());
        });

        Assert.Contains("PRODUCTION: real money on your Kalshi account", texts);
        Assert.Contains("Resting orders", texts);
        Assert.Contains("Positions", texts);
        Assert.Contains("bid (buys YES)", texts);
        Assert.Contains("2.5 YES", texts);
        Assert.Contains("Connected to Kalshi.", texts);
        Assert.Contains("KILL SWITCH", buttons);
        Assert.Contains("New order…", buttons);
        Assert.Contains("Resume", buttons);
        Assert.Contains("Cancel", buttons);
        Assert.Contains("Change…", buttons);
    }

    [Fact]
    public void TheOrderTicketShowsItsFieldsTheCheckAndTheTypedConfirmationAndRefusesAPaste()
    {
        var (texts, pasteCancelled) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var link = new FakeTradingLink();
            var ticket = OrderTicketViewModel.ForNewOrder(link, true, "KX-A", "0.40");
            ticket.CountText = "2";
            ticket.CheckCommand.RunAsync().GetAwaiter().GetResult();
            var window = new OrderTicketWindow { DataContext = ticket };
            VisualTreeKit.Realize(window);
            var box = VisualTreeKit.All<TextBox>(window).Single(b => (b.ToolTip as string)?.StartsWith("Typed, not pasted", StringComparison.Ordinal) == true);
            var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.Text, "KX-A buy yes 2 0.40"), isDragDrop: false, DataFormats.Text) { RoutedEvent = DataObject.PastingEvent };
            box.RaiseEvent(args);
            Assert.Empty(errors.Messages);
            return (VisualTreeKit.Texts(window).ToList(), args.CommandCancelled);
        });

        Assert.Contains("PRODUCTION: this sends a real order with real money.", texts);
        Assert.Contains("What would happen (nothing has been sent)", texts);
        Assert.Contains("BUY 2 YES KX-A at $0.4000", texts);
        Assert.Contains("To send it, type the order: TICKER buy|sell yes|no COUNT PRICE", texts);
        Assert.True(pasteCancelled);
    }

    [Fact]
    public void ADemoTicketAsksForNoTypingAndAChangeTicketHidesTheOrderKindBoxes()
    {
        var (demoTexts, changeTexts) = Sta.Run(() =>
        {
            var link = new FakeTradingLink();
            var demo = OrderTicketViewModel.ForNewOrder(link, false, "KX-A", "0.40");
            demo.CountText = "2";
            demo.CheckCommand.RunAsync().GetAwaiter().GetResult();
            var demoWindow = new OrderTicketWindow { DataContext = demo };
            VisualTreeKit.Realize(demoWindow);
            var change = OrderTicketViewModel.ForAmend(link, false, TradingSamples.Order());
            var changeWindow = new OrderTicketWindow { DataContext = change };
            VisualTreeKit.Realize(changeWindow);
            return (VisualTreeKit.Texts(demoWindow).ToList(), VisualTreeKit.Texts(changeWindow).ToList());
        });

        Assert.DoesNotContain("To send it, type the order: TICKER buy|sell yes|no COUNT PRICE", demoTexts);
        Assert.DoesNotContain("PRODUCTION: this sends a real order with real money.", demoTexts);
        Assert.DoesNotContain("Action", changeTexts);
        Assert.Contains(changeTexts, t => t.StartsWith("bid 2 of 2 resting at", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMarketDetailHasATradeButton()
    {
        var buttons = Sta.Run(() =>
        {
            var format = new Formatting(TimeZoneInfo.Utc);
            using var model = new MarketsViewModel(format, _t.Time, InlineContext.Instance, _dialogs);
            _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
            model.Attach(() => _t.Api);
            model.LoadAsync().GetAwaiter().GetResult();
            model.OpenAsync(model.Rows[0]).GetAwaiter().GetResult();
            var view = new MarketDetailView { DataContext = model.Detail };
            VisualTreeKit.Realize(view);
            return VisualTreeKit.All<Button>(view).Select(b => b.Content as string).ToList();
        });

        Assert.Contains("Trade…", buttons);
    }
}
