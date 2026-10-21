using System.Collections.ObjectModel;
using System.Globalization;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;
using KalTerm.Trading.Risk;

namespace KalTerm.Terminal.Trading;

/// <summary>How the Trading page writes money and counts: from the exact integers, never through floating point.</summary>
internal static class TradingFormat
{
    /// <summary>Dollars at 10^-6 as <c>$0.4000</c> (six decimals only when the value needs them).</summary>
    /// <param name="valueE6">The amount.</param>
    /// <returns>The text.</returns>
    public static string Dollars(long valueE6) => RiskChecker.DollarsText(valueE6);

    /// <summary>Dollars without the sign, for a box the person edits.</summary>
    /// <param name="valueE6">The amount.</param>
    /// <returns>The text.</returns>
    public static string Plain(long valueE6) => RiskChecker.DollarsText(valueE6).TrimStart('$');

    /// <summary>Contracts at 10^-2 without trailing zeros.</summary>
    /// <param name="valueE2">The count.</param>
    /// <returns>The text.</returns>
    public static string Count(long valueE2) => RiskChecker.CountText(valueE2);
}

/// <summary>A resting order in the grid, with what can be done to it.</summary>
internal sealed class OrderRowViewModel(OrderView order, AsyncCommand cancel, AsyncCommand amend)
{
    public string OrderId { get; } = order.OrderId;

    public string Ticker { get; } = order.Ticker;

    /// <summary>bid buys YES (or sells NO); ask sells YES (or buys NO). The price is always the YES price, as the exchange's book has it.</summary>
    public string Side { get; } = order.BookSide == "bid" ? "bid (buys YES)" : "ask (sells YES)";

    public string Price { get; } = TradingFormat.Dollars(order.YesPriceE6);

    public string Remaining { get; } = TradingFormat.Count(order.RemainingE2);

    public string Filled { get; } = TradingFormat.Count(order.FilledE2);

    public string Shard { get; } = order.Shard.ToString(CultureInfo.InvariantCulture);

    public AsyncCommand CancelCommand { get; } = cancel;

    public AsyncCommand AmendCommand { get; } = amend;
}

/// <summary>A position in the grid.</summary>
internal sealed class PositionRowViewModel(PositionView position)
{
    public string Ticker { get; } = position.Ticker;

    /// <summary>YES contracts held, or NO contracts when negative.</summary>
    public string Position { get; } = position.PositionE2 >= 0 ? $"{TradingFormat.Count(position.PositionE2)} YES" : $"{TradingFormat.Count(-position.PositionE2)} NO";

    public string Exposure { get; } = TradingFormat.Dollars(position.ExposureE6);

    public string Realized { get; } = TradingFormat.Dollars(position.RealizedE6);
}

/// <summary>A fill in the grid; the time follows the zone setting.</summary>
internal sealed class FillRowViewModel(FillView fill, Formatting format)
{
    public string Time { get; } = fill.Time is { } time ? format.Moment(time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)) : Formatting.None;

    public string Ticker { get; } = fill.Ticker;

    public string What { get; } = $"{fill.Action} {fill.Side}".Trim();

    public string Price { get; } = TradingFormat.Dollars(fill.YesPriceE6);

    public string Count { get; } = TradingFormat.Count(fill.CountE2);

    public string Fee { get; } = TradingFormat.Dollars(fill.FeeE6);
}

/// <summary>
/// The Trading page: the account on Kalshi (balances, risk meters, resting orders, positions, fills), the order ticket, cancel and change, and the kill switch. The session opens by itself when the terminal starts, in the
/// background; a failure (no trading key, no network) is shown here and never stops anything else, and Connect tries again. The kill switch works without a session too: it opens the lightest one and cancels everything.
/// Every order goes through the order service and its risk layer; this page decides nothing about money.
/// </summary>
internal sealed class TradingViewModel : ObservableObject, IDisposable
{
    /// <summary>How long opening the session may take before it is given up.</summary>
    public static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(60);

    private readonly ITradingGateway _gateway;
    private readonly Formatting _format;
    private readonly IDialogs _dialogs;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private ITradingLink? _link;
    private LinkState _state = LinkState.NotConnected;
    private string _stateText = "Not connected to Kalshi.";
    private AccountView? _account;
    private string _lastAction = string.Empty;
    private string _error = string.Empty;
    private bool _refreshQueued;
    private bool _opening;

    /// <summary>Creates the page.</summary>
    /// <param name="gateway">Opens trading sessions.</param>
    /// <param name="format">Writes times in the chosen zone.</param>
    /// <param name="dialogs">Asks for confirmation and shows the order ticket.</param>
    /// <param name="time">The clock the opening limit uses.</param>
    /// <param name="context">Where updates from the account stream are handed to the page; the UI thread's context when omitted.</param>
    public TradingViewModel(ITradingGateway gateway, Formatting format, IDialogs dialogs, TimeProvider time, SynchronizationContext? context = null)
    {
        _gateway = gateway;
        _format = format;
        _dialogs = dialogs;
        _time = time;
        _context = context ?? SynchronizationContext.Current;
        ConnectCommand = new AsyncCommand(ConnectAsync, ex => Error = ex.Message, () => _state is LinkState.NotConnected or LinkState.Failed);
        NewOrderCommand = new AsyncCommand(() => OpenTicketAsync(string.Empty, null), ex => Error = ex.Message, () => _state == LinkState.Connected);
        KillCommand = new AsyncCommand(KillAsync, ex => Error = SayFailure(ex), () => _state != LinkState.Connecting);
        ResumeCommand = new AsyncCommand(ResumeAsync, ex => Error = ex.Message, () => _state == LinkState.Connected && Halted);
        _format.ZoneChanged += Refresh;
    }

    public string Title { get; } = "Trading";

    public ObservableCollection<OrderRowViewModel> Orders { get; } = [];

    public ObservableCollection<PositionRowViewModel> Positions { get; } = [];

    public ObservableCollection<FillRowViewModel> Fills { get; } = [];

    public AsyncCommand ConnectCommand { get; }

    public AsyncCommand NewOrderCommand { get; }

    public AsyncCommand KillCommand { get; }

    public AsyncCommand ResumeCommand { get; }

    public LinkState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                RaiseCommands();
                Raise(nameof(IsConnected));
            }
        }
    }

    public bool IsConnected => _state == LinkState.Connected;

    /// <summary>Why there is no session, or that it is open.</summary>
    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    /// <summary>The real account (production), not the demo one.</summary>
    public bool Production => _account?.Production == true;

    /// <summary>PRODUCTION (real money) or DEMO, empty until a session is open.</summary>
    public string EnvironmentText => _account is null ? string.Empty : _account.Production ? "PRODUCTION: real money on your Kalshi account" : "DEMO: no real money";

    public bool Halted => _account?.Halted == true;

    /// <summary>Why new orders are refused after the kill switch or the daily-loss limit.</summary>
    public string HaltText => Halted ? $"HALTED: new orders are refused ({(_account!.HaltReason.Length == 0 ? "kill switch" : _account.HaltReason)}). Resume allows them again." : string.Empty;

    /// <summary>The account stream is not in sync or is down: new orders are refused until it is.</summary>
    public string SyncText => _account is null || _account.Synced ? string.Empty : $"The live account view is not in sync{(_account.Fault.Length > 0 ? $" ({_account.Fault})" : string.Empty)}: new orders are refused until it is.";

    public bool HasSyncProblem => SyncText.Length > 0;

    public string BalanceText => _account is null ? string.Empty : _account.Shards.Count == 0
        ? "reading the balance…"
        : string.Join("   ", _account.Shards.Select(s => $"shard {s.Index}: {TradingFormat.Dollars(s.BalanceE6)}")) + $"   total: {TradingFormat.Dollars(_account.Shards.Sum(s => s.BalanceE6))}";

    public string ExposureText => _account is null ? string.Empty : $"{TradingFormat.Dollars(_account.ExposureE6)} of {TradingFormat.Dollars(_account.Limits.MaxOpenExposureE6)}";

    public string DayText => _account is null ? string.Empty : _account.RealizedTodayE6 >= 0
        ? $"{TradingFormat.Dollars(_account.RealizedTodayE6)} made today (trading stops at {TradingFormat.Dollars(_account.Limits.MaxDailyLossE6)} lost)"
        : $"{TradingFormat.Dollars(-_account.RealizedTodayE6)} lost today of {TradingFormat.Dollars(_account.Limits.MaxDailyLossE6)}";

    public string OpenOrdersText => _account is null ? string.Empty : $"{_account.Orders.Count} of {_account.Limits.MaxOpenOrders}";

    /// <summary>The limits of one order, as the risk file has them.</summary>
    public string LimitsText => _account is null ? string.Empty : $"One order: at most {TradingFormat.Count(_account.Limits.MaxOrderCountE2)} contracts and {TradingFormat.Dollars(_account.Limits.MaxOrderNotionalE6)} including the fee. Limits change only by editing risk.json.";

    /// <summary>What the last cancel, change, send or kill did.</summary>
    public string LastAction
    {
        get => _lastAction;
        private set
        {
            if (Set(ref _lastAction, value))
            {
                Raise(nameof(HasAction));
            }
        }
    }

    public bool HasAction => _lastAction.Length > 0;

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>Opens the session in the background. Never throws: a failure becomes the page's state, so the rest of the terminal starts regardless.</summary>
    /// <returns>A task that completes when the session is open or has failed.</returns>
    public async Task ConnectAsync()
    {
        if (_opening || _state is LinkState.Connected or LinkState.Connecting)
        {
            return;
        }

        _opening = true;
        State = LinkState.Connecting;
        StateText = "Connecting to Kalshi…";
        Error = string.Empty;
        using var limit = new CancellationTokenSource(OpenLimit, _time);
        try
        {
            var link = await _gateway.OpenAsync(limit.Token).ConfigureAwait(true);
            _link = link;
            link.Changed += OnLinkChanged;
            _account = link.Account;
            State = LinkState.Connected;
            StateText = "Connected to Kalshi.";
            Refresh();
        }
        catch (OperationCanceledException)
        {
            State = LinkState.Failed;
            StateText = "Kalshi did not answer in time. Press Connect to try again.";
        }
        catch (Exception ex) when (TradingProblems.IsExpected(ex))
        {
            State = LinkState.Failed;
            StateText = TradingProblems.Say(ex);
        }
        finally
        {
            _opening = false;
            RaiseCommands();
        }
    }

    /// <summary>Opens the order ticket (a new order).</summary>
    /// <param name="ticker">The market to start with, or empty.</param>
    /// <param name="price">A price to start with (dollars), or <see langword="null"/>.</param>
    /// <returns>A task that completes when the ticket is closed.</returns>
    public async Task OpenTicketAsync(string ticker, string? price)
    {
        if (_link is null)
        {
            return;
        }

        var ticket = OrderTicketViewModel.ForNewOrder(_link, _account!.Production, ticker, price);
        await _dialogs.ShowOrderTicketAsync(ticket).ConfigureAwait(true);
        NoteTicket(ticket);
    }

    /// <summary>Lets go of the session.</summary>
    public void Dispose()
    {
        _format.ZoneChanged -= Refresh;
        if (_link is { } link)
        {
            link.Changed -= OnLinkChanged;
            _link = null;
            _ = link.DisposeAsync().AsTask();
        }
    }

    private async Task ChangeOrderAsync(OrderView order)
    {
        if (_link is null)
        {
            return;
        }

        var ticket = OrderTicketViewModel.ForAmend(_link, _account!.Production, order);
        await _dialogs.ShowOrderTicketAsync(ticket).ConfigureAwait(true);
        NoteTicket(ticket);
    }

    private async Task CancelOrderAsync(OrderView order)
    {
        if (_link is not { } link
            || !await _dialogs.ConfirmAsync("Cancel order", $"Cancel the resting order on {order.Ticker} ({(order.BookSide == "bid" ? "bid" : "ask")} {TradingFormat.Count(order.RemainingE2)} at {TradingFormat.Dollars(order.YesPriceE6)})?").ConfigureAwait(true))
        {
            return;
        }

        LastAction = await link.CancelAsync(order.OrderId, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task KillAsync()
    {
        if (!await _dialogs.ConfirmAsync("Kill switch", "Cancel every open order and refuse new orders until you press Resume?").ConfigureAwait(true))
        {
            return;
        }

        var result = _link is { } link
            ? await link.KillAsync(CancellationToken.None).ConfigureAwait(true)
            : await _gateway.KillAsync(CancellationToken.None).ConfigureAwait(true);
        var lines = new List<string> { "Trading is HALTED (new orders are refused until you press Resume).", $"Cancelled {result.Cancelled} order(s) in the follow-up passes." };
        if (result.Remaining.Count > 0)
        {
            lines.Add($"STILL RESTING: {string.Join(", ", result.Remaining)}");
        }

        lines.AddRange(result.Errors.Select(e => $"Problem: {e}"));
        if (result.Clean)
        {
            lines.Add("No orders are resting.");
        }

        LastAction = string.Join(Environment.NewLine, lines);
        Refresh();
    }

    private async Task ResumeAsync()
    {
        if (_link is not { } link || !await _dialogs.ConfirmAsync("Resume trading", "Allow new orders again?").ConfigureAwait(true))
        {
            return;
        }

        link.Resume();
        LastAction = "The halt is cleared; new orders are allowed again.";
        Refresh();
    }

    private void NoteTicket(OrderTicketViewModel ticket)
    {
        if (ticket.HasResult)
        {
            LastAction = string.Join(Environment.NewLine, ticket.Result);
        }
        else if (ticket.Unknown)
        {
            LastAction = ticket.Error;
        }

        Refresh();
    }

    private static string SayFailure(Exception ex) => TradingProblems.IsExpected(ex) ? TradingProblems.Say(ex) : ex.Message;

    private void OnLinkChanged()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        if (_context is { } context)
        {
            context.Post(_ => Refresh(), null);
        }
        else
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        _refreshQueued = false;
        if (_link is not { } link)
        {
            return;
        }

        var account = link.Account;
        _account = account;
        Orders.Clear();
        foreach (var order in account.Orders)
        {
            var captured = order;
            Orders.Add(new OrderRowViewModel(
                order,
                new AsyncCommand(() => CancelOrderAsync(captured), ex => Error = SayFailure(ex)),
                new AsyncCommand(() => ChangeOrderAsync(captured), ex => Error = SayFailure(ex))));
        }

        Positions.Clear();
        foreach (var position in account.Positions)
        {
            Positions.Add(new PositionRowViewModel(position));
        }

        Fills.Clear();
        foreach (var fill in account.Fills)
        {
            Fills.Add(new FillRowViewModel(fill, _format));
        }

        foreach (var name in new[] { nameof(Production), nameof(EnvironmentText), nameof(Halted), nameof(HaltText), nameof(SyncText), nameof(HasSyncProblem), nameof(BalanceText), nameof(ExposureText), nameof(DayText), nameof(OpenOrdersText), nameof(LimitsText) })
        {
            Raise(name);
        }

        RaiseCommands();
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        NewOrderCommand.RaiseCanExecuteChanged();
        KillCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
    }
}
