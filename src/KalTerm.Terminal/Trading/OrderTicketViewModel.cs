using KalTerm.Core.Money;
using KalTerm.Terminal.Mvvm;
using KalTerm.Trading.Risk;

namespace KalTerm.Terminal.Trading;

/// <summary>
/// The order ticket: what the person wants (a new order, or a change to a resting one), the check that says what would happen, and the send. Nothing is sent until the check has been run on exactly what is on the ticket and
/// the risk layer approved it; changing anything on the ticket throws the check away. In production Send also needs the typed confirmation (<c>TICKER buy|sell yes|no COUNT PRICE</c>), judged by the order service itself.
/// After an unclear answer (the order may or may not have reached Kalshi) the ticket cannot send again: the order is looked for, not repeated.
/// </summary>
internal sealed class OrderTicketViewModel : ObservableObject
{
    /// <summary>The choices of the action box.</summary>
    public static readonly IReadOnlyList<string> ActionChoices = ["Buy", "Sell"];

    /// <summary>The choices of the outcome box.</summary>
    public static readonly IReadOnlyList<string> OutcomeChoices = ["Yes", "No"];

    private readonly ITradingLink _link;
    private readonly OrderView? _amending;
    private string _ticker;
    private string _action = ActionChoices[0];
    private string _outcome = OutcomeChoices[0];
    private string _countText = string.Empty;
    private string _priceText;
    private bool _immediateOrCancel;
    private bool _postOnly;
    private string _typed = string.Empty;
    private OrderPlan? _plan;
    private bool _touched;
    private bool _busy;
    private bool _sent;
    private bool _unknown;
    private string _error = string.Empty;
    private IReadOnlyList<string> _result = [];

    private OrderTicketViewModel(ITradingLink link, bool production, string ticker, string? price, OrderView? amending)
    {
        _link = link;
        _amending = amending;
        RequiresTyping = production;
        _ticker = ticker;
        _priceText = price ?? string.Empty;
        CheckCommand = new AsyncCommand(CheckAsync, ex => Fail(ex), () => CanCheck);
        SendCommand = new AsyncCommand(SendAsync, ex => Fail(ex), () => CanSend);
        CloseCommand = new RelayCommand(() => Closed?.Invoke());
    }

    /// <summary>The person is done with the ticket.</summary>
    public event Action? Closed;

    /// <summary>A ticket for a new order.</summary>
    /// <param name="link">The session.</param>
    /// <param name="production">Whether this is the real account.</param>
    /// <param name="ticker">The market to start with, or empty.</param>
    /// <param name="price">A price to start with (dollars), or <see langword="null"/>.</param>
    /// <returns>The ticket.</returns>
    public static OrderTicketViewModel ForNewOrder(ITradingLink link, bool production, string ticker, string? price)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(ticker);
        return new OrderTicketViewModel(link, production, ticker, price, null);
    }

    /// <summary>A ticket that changes a resting order's price and/or total count.</summary>
    /// <param name="link">The session.</param>
    /// <param name="production">Whether this is the real account.</param>
    /// <param name="order">The resting order.</param>
    /// <returns>The ticket.</returns>
    public static OrderTicketViewModel ForAmend(ITradingLink link, bool production, OrderView order)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(order);
        return new OrderTicketViewModel(link, production, order.Ticker, TradingFormat.Plain(order.YesPriceE6), order) { _countText = RiskChecker.CountText(order.InitialE2) };
    }

    public string Title => _amending is null ? "New order" : "Change order";

    public bool IsAmend => _amending is not null;

    /// <summary>A new order (not a change): the action, outcome and order-kind boxes show.</summary>
    public bool IsNew => _amending is null;

    /// <summary>The typed confirmation shows: production, checked and approved, not yet sent.</summary>
    public bool ShowTyping => RequiresTyping && PlanOk && !_sent && !_unknown;

    /// <summary>For a change: which order, in words.</summary>
    public string AmendingText => _amending is null ? string.Empty : $"{_amending.BookSide} {RiskChecker.CountText(_amending.RemainingE2)} of {RiskChecker.CountText(_amending.InitialE2)} resting at {RiskChecker.DollarsText(_amending.YesPriceE6)} (YES price) on {_amending.Ticker}. Give the new YES price and/or the new total count.";

    /// <summary>Whether production orders need the typed confirmation.</summary>
    public bool RequiresTyping { get; }

    public RelayCommand CloseCommand { get; }

    public AsyncCommand CheckCommand { get; }

    public AsyncCommand SendCommand { get; }

    public string Ticker
    {
        get => _ticker;
        set => Edit(ref _ticker, value);
    }

    public string Action
    {
        get => _action;
        set => Edit(ref _action, value);
    }

    public string Outcome
    {
        get => _outcome;
        set => Edit(ref _outcome, value);
    }

    /// <summary>Contracts; for a change, the new total (empty keeps it).</summary>
    public string CountText
    {
        get => _countText;
        set => Edit(ref _countText, value);
    }

    /// <summary>The limit price in dollars of the side traded; for a change, the new YES price (empty keeps it).</summary>
    public string PriceText
    {
        get => _priceText;
        set => Edit(ref _priceText, value);
    }

    public bool ImmediateOrCancel
    {
        get => _immediateOrCancel;
        set
        {
            if (_immediateOrCancel != value)
            {
                _immediateOrCancel = value;
                Raise();
                Edited();
            }
        }
    }

    public bool PostOnly
    {
        get => _postOnly;
        set
        {
            if (_postOnly != value)
            {
                _postOnly = value;
                Raise();
                Edited();
            }
        }
    }

    /// <summary>What was typed to confirm.</summary>
    public string TypedText
    {
        get => _typed;
        set
        {
            if (Set(ref _typed, value))
            {
                Raise(nameof(PhraseOk));
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What is wrong with what was typed so far, or <see langword="null"/> when the order can be checked. Nothing is said while the fields are empty.</summary>
    public string? Problem
    {
        get
        {
            if (_amending is not null)
            {
                return AmendValues(out _, out _, out var changeProblem) ? null : changeProblem;
            }

            return Entry(out var problem) is null ? problem : null;
        }
    }

    public bool HasProblem => _touched && Problem is not null;

    /// <summary>The check that was run, or <see langword="null"/> when none has been or the ticket changed since.</summary>
    public OrderPlan? Plan
    {
        get => _plan;
        private set
        {
            if (Set(ref _plan, value))
            {
                Raise(nameof(PlanLines));
                Raise(nameof(HasPlan));
                Raise(nameof(PlanOk));
                Raise(nameof(PhraseOk));
                Raise(nameof(ShowTyping));
                CommandsChanged();
            }
        }
    }

    public bool HasPlan => _plan is not null;

    public IReadOnlyList<string> PlanLines => _plan?.Lines ?? [];

    /// <summary>The risk layer approved what was checked.</summary>
    public bool PlanOk => _plan?.Ok == true;

    /// <summary>The typed confirmation matches the order that was checked.</summary>
    public bool PhraseOk => _plan is not null && _link.PhraseMatches(_plan, _typed);

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                CommandsChanged();
            }
        }
    }

    /// <summary>The order went out; the ticket shows what happened.</summary>
    public bool Sent
    {
        get => _sent;
        private set
        {
            if (Set(ref _sent, value))
            {
                Raise(nameof(ShowTyping));
                CommandsChanged();
            }
        }
    }

    /// <summary>The answer was unclear: it is not known whether the order went out, and the ticket will not send it again.</summary>
    public bool Unknown
    {
        get => _unknown;
        private set
        {
            if (Set(ref _unknown, value))
            {
                Raise(nameof(ShowTyping));
                CommandsChanged();
            }
        }
    }

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

    /// <summary>What happened to the order that was sent.</summary>
    public IReadOnlyList<string> Result
    {
        get => _result;
        private set
        {
            if (Set(ref _result, value))
            {
                Raise(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _result.Count > 0;

    /// <summary>The ticket can be checked now.</summary>
    public bool CanCheck => !_busy && !_sent && !_unknown && Problem is null;

    /// <summary>The ticket can be sent now: checked, approved, and (in production) confirmed by typing.</summary>
    public bool CanSend => !_busy && !_sent && !_unknown && PlanOk && (!RequiresTyping || PhraseOk);

    private OrderEntry? Entry(out string? problem)
    {
        problem = null;
        if (_ticker.Trim().Length == 0)
        {
            problem = "Type the market's ticker.";
            return null;
        }

        if (!KalTerm.Core.Money.Contracts.TryParse(_countText.Trim(), out var count) || count.Hundredths <= 0)
        {
            problem = "The count is a positive number of contracts with at most two decimals.";
            return null;
        }

        if (!Dollars.TryParse(_priceText.Trim(), out var price) || price.Micros <= 0 || price.Micros >= Dollars.Scale)
        {
            problem = "The price is dollars strictly between 0 and 1, for example 0.40.";
            return null;
        }

        return new OrderEntry(_ticker.Trim(), _action == ActionChoices[0], _outcome == OutcomeChoices[0], count.Hundredths, price.Micros, _immediateOrCancel, _postOnly);
    }

    private bool AmendValues(out long? price, out long? count, out string? problem)
    {
        price = null;
        count = null;
        problem = null;
        if (_priceText.Trim().Length > 0)
        {
            if (!Dollars.TryParse(_priceText.Trim(), out var parsed) || parsed.Micros <= 0 || parsed.Micros >= Dollars.Scale)
            {
                problem = "The price is dollars strictly between 0 and 1, for example 0.40.";
                return false;
            }

            price = parsed.Micros;
        }

        if (_countText.Trim().Length > 0)
        {
            if (!KalTerm.Core.Money.Contracts.TryParse(_countText.Trim(), out var parsed) || parsed.Hundredths <= 0)
            {
                problem = "The count is a positive number of contracts with at most two decimals.";
                return false;
            }

            count = parsed.Hundredths;
        }

        if (price is null && count is null)
        {
            problem = "Give a new price or a new total count.";
            return false;
        }

        return true;
    }

    private async Task CheckAsync()
    {
        Error = string.Empty;
        Busy = true;
        try
        {
            Plan = null;
            if (_amending is { } order)
            {
                if (!AmendValues(out var price, out var count, out _))
                {
                    return;
                }

                Plan = await _link.PrepareAmendAsync(order.OrderId, price, count, CancellationToken.None).ConfigureAwait(true);
            }
            else if (Entry(out _) is { } entry)
            {
                Plan = await _link.PrepareAsync(entry, CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task SendAsync()
    {
        if (_plan is not { } plan)
        {
            return;
        }

        Error = string.Empty;
        Busy = true;
        try
        {
            Result = await _link.SendAsync(plan, CancellationToken.None).ConfigureAwait(true);
            Sent = true;
        }
        catch (Exception ex) when (TradingProblems.IsExpected(ex))
        {
            Error = TradingProblems.Say(ex);
            if (TradingProblems.IsUnknown(ex))
            {
                Unknown = true;
            }
            else
            {
                Plan = null;   // whatever was approved is not what the exchange has now: it is checked again
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void Fail(Exception ex) => Error = TradingProblems.IsExpected(ex) ? TradingProblems.Say(ex) : ex.Message;

    private void Edit(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name))
        {
            _touched = true;
            Edited();
        }
    }

    /// <summary>A field changed: what was checked is not what is on the ticket any more.</summary>
    private void Edited()
    {
        Plan = null;
        TypedText = string.Empty;
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        CommandsChanged();
    }

    private void CommandsChanged()
    {
        Raise(nameof(CanCheck));
        Raise(nameof(CanSend));
        CheckCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
    }
}
