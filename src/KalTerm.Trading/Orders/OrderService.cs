using KalTerm.Core.Fees;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Orders;

/// <summary>The order was refused by the risk layer or the market. <see cref="Verdict"/> has every reason.</summary>
public sealed class OrderRefusedException : Exception
{
    public OrderRefusedException()
    {
    }

    public OrderRefusedException(string message)
        : base(message)
    {
    }

    public OrderRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OrderRefusedException(Verdict verdict)
        : base("order refused: " + string.Join("; ", (verdict ?? throw new ArgumentNullException(nameof(verdict))).Refusals.Select(r => r.Message)))
    {
        Verdict = verdict;
    }

    /// <summary>The verdict with all its reasons.</summary>
    public Verdict? Verdict { get; }
}

/// <summary>What would happen is no longer what was shown; prepare again and confirm again.</summary>
public sealed class PlanChangedException : Exception
{
    public PlanChangedException()
    {
    }

    public PlanChangedException(string message)
        : base(message)
    {
    }

    public PlanChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Kalshi refused a shard transfer; the order was not placed.</summary>
public sealed class TransferFailedException : Exception
{
    public TransferFailedException()
    {
    }

    public TransferFailedException(string message)
        : base(message)
    {
    }

    public TransferFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A shard transfer got no clear answer. It stays recorded as pending and the order was not placed. Look at the balance before trying again.</summary>
public sealed class TransferUnknownException : Exception
{
    public TransferUnknownException()
    {
    }

    public TransferUnknownException(string message)
        : base(message)
    {
    }

    public TransferUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The transfers finished (or were recorded) but the shard does not hold what the order needs.</summary>
public sealed class FundingShortException : Exception
{
    public FundingShortException()
    {
    }

    public FundingShortException(string message)
        : base(message)
    {
    }

    public FundingShortException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An order to cancel or amend is not known, or is not resting.</summary>
public sealed class OrderNotFoundException : Exception
{
    public OrderNotFoundException()
    {
    }

    public OrderNotFoundException(string message)
        : base(message)
    {
    }

    public OrderNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The market reads the order service needs, from Kalshi directly.</summary>
public interface IMarketRest
{
    /// <summary>A market.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The market.</returns>
    Task<Market> MarketAsync(string ticker, CancellationToken cancellationToken);

    /// <summary>An event.</summary>
    /// <param name="eventTicker">The event.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The event.</returns>
    Task<KalshiEvent> EventAsync(string eventTicker, CancellationToken cancellationToken);

    /// <summary>A series.</summary>
    /// <param name="seriesTicker">The series.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The series.</returns>
    Task<Series> SeriesAsync(string seriesTicker, CancellationToken cancellationToken);

    /// <summary>One order.</summary>
    /// <param name="orderId">Kalshi's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The order.</returns>
    Task<Order> OrderAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>Every resting order.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The orders.</returns>
    IAsyncEnumerable<Order> RestingOrdersAsync(CancellationToken cancellationToken);
}

/// <summary>The real REST client behind <see cref="IMarketRest"/>.</summary>
/// <param name="client">The signed read client.</param>
public sealed class KalshiMarketRest(KalshiRestClient client) : IMarketRest
{
    /// <inheritdoc />
    public Task<Market> MarketAsync(string ticker, CancellationToken cancellationToken) => client.MarketAsync(ticker, cancellationToken);

    /// <inheritdoc />
    public Task<KalshiEvent> EventAsync(string eventTicker, CancellationToken cancellationToken) => client.EventAsync(eventTicker, cancellationToken);

    /// <inheritdoc />
    public Task<Series> SeriesAsync(string seriesTicker, CancellationToken cancellationToken) => client.SeriesAsync(seriesTicker, cancellationToken);

    /// <inheritdoc />
    public Task<Order> OrderAsync(string orderId, CancellationToken cancellationToken) => client.OrderAsync(orderId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Order> RestingOrdersAsync(CancellationToken cancellationToken) =>
        client.IterateOrdersAsync(KalshiQuery.Empty.With("status", "resting"), cancellationToken);
}

/// <summary>The writes the order service makes (<see cref="KalshiTradingClient"/> provides them).</summary>
public interface ITradingApi
{
    /// <summary>Sends a new order, once.</summary>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The created order.</returns>
    Task<CreatedOrder> CreateOrderAsync(OrderRequest order, CancellationToken cancellationToken = default);

    /// <summary>Changes the price and total size of a resting order.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="side">The YES-book side.</param>
    /// <param name="priceE6">The new price.</param>
    /// <param name="countE2">The new total count.</param>
    /// <param name="exchangeIndex">The shard.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The changed order.</returns>
    Task<ChangedOrder> AmendOrderAsync(string orderId, string ticker, BookSide side, long priceE6, long countE2, int exchangeIndex, CancellationToken cancellationToken = default);

    /// <summary>Cancels one order.</summary>
    /// <param name="target">The order.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    Task<CancelResult> CancelOrderAsync(CancelTarget target, CancellationToken cancellationToken = default);

    /// <summary>Cancels several orders.</summary>
    /// <param name="targets">The orders.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A result per order.</returns>
    Task<IReadOnlyList<CancelResult>> CancelOrdersAsync(IReadOnlyList<CancelTarget> targets, CancellationToken cancellationToken = default);

    /// <summary>Cancels every resting order.</summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A task that completes when accepted.</returns>
    Task CancelAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves collateral between shards, sent at most once.</summary>
    /// <param name="sourceShard">The shard the money leaves.</param>
    /// <param name="destinationShard">The shard it goes to.</param>
    /// <param name="amountE6">The amount.</param>
    /// <param name="transferId">An id for the transfer.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Kalshi's transfer id.</returns>
    Task<string> TransferAsync(int sourceShard, int destinationShard, long amountE6, string transferId, CancellationToken cancellationToken = default);
}

/// <summary>What the person wants, in terms of the outcome they trade.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Action">Buy or sell.</param>
/// <param name="Outcome">YES or NO.</param>
/// <param name="CountE2">Contracts in hundredths.</param>
/// <param name="PriceE6">The limit price of the outcome, in millionths of a dollar.</param>
/// <param name="TimeInForce">How long the order lives.</param>
/// <param name="PostOnly">Never take liquidity.</param>
public sealed record TradeRequest(string Ticker, TradeAction Action, TradeOutcome Outcome, long CountE2, long PriceE6, TimeInForce TimeInForce = TimeInForce.GoodTillCanceled, bool PostOnly = false);

/// <summary>What Kalshi says about a market that an order needs.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="FeeMultiplierE6">The series' taker fee multiplier in millionths, or <see langword="null"/> when it cannot be modelled.</param>
/// <param name="Status">Its status.</param>
public sealed record MarketInfo(string Ticker, int ExchangeIndex, long? FeeMultiplierE6, string Status)
{
    /// <summary>Whether orders can trade in it.</summary>
    public bool Tradable => Status is "active" or "open";
}

/// <summary>An order checked and priced, not sent. Says exactly what would happen, including shard funding, so a person can confirm it.</summary>
/// <param name="Request">What was asked.</param>
/// <param name="Market">The market.</param>
/// <param name="Intent">The risk intent.</param>
/// <param name="Verdict">The verdict (may be a refusal).</param>
/// <param name="Side">The YES-book side to send.</param>
/// <param name="PriceE6">The YES price to send.</param>
/// <param name="Replaces">For an amend, the resting order this replaces.</param>
/// <param name="TotalCountE2">For an amend, the order's new total count.</param>
public sealed record Prepared(TradeRequest Request, MarketInfo Market, OrderIntent Intent, Verdict Verdict, BookSide Side, long PriceE6, string? Replaces = null, long? TotalCountE2 = null);

/// <summary>An order that was placed.</summary>
/// <param name="Created">Kalshi's reply.</param>
/// <param name="ClientOrderId">The id sent.</param>
/// <param name="Approved">The risk proof.</param>
/// <param name="Transfers">The shard transfers made first.</param>
public sealed record Placement(CreatedOrder Created, string ClientOrderId, ApprovedOrder Approved, IReadOnlyList<Transfer> Transfers);

/// <summary>An order that was amended.</summary>
/// <param name="Result">Kalshi's reply.</param>
/// <param name="OrderId">The order.</param>
/// <param name="Approved">The risk proof.</param>
/// <param name="Transfers">The shard transfers made first.</param>
public sealed record Amendment(ChangedOrder Result, string OrderId, ApprovedOrder Approved, IReadOnlyList<Transfer> Transfers);

/// <summary>What the kill switch did.</summary>
public sealed class KillReport
{
    /// <summary>Orders cancelled by the batch passes.</summary>
    public List<string> Cancelled { get; } = [];

    /// <summary>Orders still resting at the end.</summary>
    public List<string> Remaining { get; set; } = [];

    /// <summary>What went wrong along the way.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Whether nothing is left resting and nothing went wrong.</summary>
    public bool Clean => Remaining.Count == 0 && Errors.Count == 0;
}

/// <summary>The outcome of cancelling one named order.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Ok">Whether it was cancelled.</param>
/// <param name="Message">What happened.</param>
public sealed record CancelOutcome(string OrderId, bool Ok, string Message);

/// <summary>
/// The one path from a wish ("buy 2 YES at 40c") to an order at Kalshi. <see cref="PrepareAsync"/> is read-only: it looks up the market (shard and fee), refreshes the balance and asks the risk layer; the
/// <see cref="Prepared"/> it returns says exactly what would happen, including any shard funding. <see cref="ExecuteAsync"/> re-checks with fresh data (refusing if the plan changed since it was shown), funds the shard,
/// re-checks once more and only then sends the order built from the risk layer's <see cref="ApprovedOrder"/>. Cancels and the kill switch are never refused. The kill switch halts first (saved, so a restart cannot undo it),
/// then cancels everything and looks again until nothing rests: the list, not the call, is what proves it worked.
/// </summary>
public sealed class OrderService : IAsyncDisposable
{
    /// <summary>How many look-and-cancel passes the kill switch makes.</summary>
    public const int KillPasses = 10;

    private const int FundingPolls = 20;
    private static readonly TimeSpan FundingPoll = TimeSpan.FromSeconds(0.5);

    private readonly IMarketRest _rest;
    private readonly ITradingApi _trading;
    private readonly AccountTracker _tracker;
    private readonly RiskPolicy _policy;
    private readonly bool _production;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;
    private readonly ITradingAudit? _audit;
    private readonly Dictionary<string, long?> _multipliers = [];
    private CancellationTokenSource? _guardStop;
    private Task? _guard;

    /// <param name="rest">Market reads.</param>
    /// <param name="trading">The writes.</param>
    /// <param name="tracker">The account view.</param>
    /// <param name="policy">The limits.</param>
    /// <param name="production">Whether this is the production account.</param>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="newId">Makes client order and transfer ids; UUIDs when omitted.</param>
    /// <param name="audit">Receives each verdict and kill; nothing when omitted.</param>
    public OrderService(IMarketRest rest, ITradingApi trading, AccountTracker tracker, RiskPolicy policy, bool production, TimeProvider? time = null, Func<string>? newId = null, ITradingAudit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(trading);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(policy);
        _rest = rest;
        _trading = trading;
        _tracker = tracker;
        _policy = policy;
        _production = production;
        _time = time ?? TimeProvider.System;
        _newId = newId ?? OrderRequest.NewClientOrderId;
        _audit = audit;
    }

    /// <summary>The YES-book side and YES price for an order in terms of an outcome. Buying YES is a bid, selling YES an ask; buying NO at q is selling YES at 1 - q (an ask), selling NO at q is buying YES at 1 - q (a bid).</summary>
    /// <param name="outcome">YES or NO.</param>
    /// <param name="action">Buy or sell.</param>
    /// <param name="ownPriceE6">The price of the outcome traded.</param>
    /// <returns>The side and the YES price.</returns>
    public static (BookSide Side, long YesPriceE6) ToV2(TradeOutcome outcome, TradeAction action, long ownPriceE6) =>
        ((action == TradeAction.Buy) == (outcome == TradeOutcome.Yes) ? BookSide.Bid : BookSide.Ask, outcome == TradeOutcome.Yes ? ownPriceE6 : 1_000_000 - ownPriceE6);

    /// <summary>Plain lines saying exactly what will happen, for the confirmation prompt.</summary>
    /// <param name="prepared">The prepared order.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Describe(Prepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var request = prepared.Request;
        var verdict = prepared.Verdict;
        var tif = request.TimeInForce switch
        {
            TimeInForce.GoodTillCanceled => "good_till_canceled",
            TimeInForce.ImmediateOrCancel => "immediate_or_cancel",
            _ => "fill_or_kill",
        };
        var lines = new List<string>
        {
            $"{request.Action.ToString().ToUpperInvariant()} {RiskChecker.CountText(request.CountE2)} {request.Outcome.ToString().ToUpperInvariant()} {request.Ticker} at {RiskChecker.DollarsText(request.PriceE6)} limit ({tif}{(request.PostOnly ? ", post-only" : string.Empty)}), shard {prepared.Market.ExchangeIndex}",
        };
        if (verdict.WorstCaseCostE6 is { } cost && verdict.FeeE6 is { } fee)
        {
            lines.Add($"worst-case cost {RiskChecker.DollarsText(cost)} (includes up to {RiskChecker.DollarsText(fee)} fee)");
        }

        lines.AddRange(verdict.Funding.Select(t => $"first moves {RiskChecker.DollarsText(t.AmountE6)} from shard {t.Source} to shard {t.Destination}"));
        lines.AddRange(verdict.Refusals.Select(r => $"REFUSED: {r.Message}"));
        return lines;
    }

    /// <summary>Whether what was typed is <c>TICKER buy|sell yes|no COUNT PRICE</c> for this request, with the numbers equal as numbers (0.4 is 0.40).</summary>
    /// <param name="typed">What the person typed.</param>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    public static bool TypedPhraseMatches(string typed, TradeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parts = (typed ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        return decimal.TryParse(parts[3], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var count)
            && decimal.TryParse(parts[4], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price)
            && parts[0] == request.Ticker
            && string.Equals(parts[1], request.Action.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], request.Outcome.ToString(), StringComparison.OrdinalIgnoreCase)
            && count * 100 == request.CountE2
            && price * 1_000_000 == request.PriceE6;
    }

    /// <summary>Shard, status and fee of a market, straight from Kalshi (not from the server).</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The market info.</returns>
    /// <exception cref="OrderRefusedException">Kalshi did not say which shard the market is on.</exception>
    public async Task<MarketInfo> MarketInfoAsync(string ticker, CancellationToken cancellationToken = default)
    {
        var market = await _rest.MarketAsync(ticker, cancellationToken).ConfigureAwait(false);
        if (market.ExchangeIndex is not { } shard)
        {
            throw new OrderRefusedException(new Verdict(null, [new Refusal(RiskCode.UnknownShard, $"{ticker}: Kalshi did not say which shard")], null, null, []));
        }

        if (!_multipliers.TryGetValue(market.EventTicker, out var multiplier))
        {
            var kalshiEvent = await _rest.EventAsync(market.EventTicker, cancellationToken).ConfigureAwait(false);
            var series = await _rest.SeriesAsync(kalshiEvent.SeriesTicker, cancellationToken).ConfigureAwait(false);
            multiplier = KalshiFees.TakerMultiplier(series.FeeType, series.FeeMultiplier?.Micros)?.Micros;
            _multipliers[market.EventTicker] = multiplier;
        }

        return new MarketInfo(ticker, shard, multiplier, market.Status);
    }

    /// <summary>Read-only: never sends anything; the verdict may be a refusal.</summary>
    /// <param name="request">The order wanted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What would happen.</returns>
    /// <exception cref="ArgumentException">The price is finer than Kalshi takes (checked here, before any shard transfer has moved money).</exception>
    public async Task<Prepared> PrepareAsync(TradeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var market = await MarketInfoAsync(request.Ticker, cancellationToken).ConfigureAwait(false);
        var intent = IntentFrom(request, market);
        var (side, price) = ToV2(request.Outcome, request.Action, intent.PriceE6);
        Wire(request, intent, side, price, "preview").Members();   // a price finer than Kalshi takes must fail here
        var verdict = await VerdictAsync(intent, market, null, cancellationToken).ConfigureAwait(false);
        Record("prepare", request.Ticker, verdict, intent);
        return new Prepared(request, market, intent, verdict, side, price);
    }

    /// <summary>
    /// Read-only: checks the order as it would be after the amend, with the old one left out of the account so its exposure is not counted twice. <paramref name="countE2"/> is the new TOTAL count (what Kalshi's amend takes);
    /// the risk check uses what would still be unfilled.
    /// </summary>
    /// <param name="orderId">The resting order.</param>
    /// <param name="priceE6">The new YES price, if changing it.</param>
    /// <param name="countE2">The new total count, if changing it.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What would happen.</returns>
    public async Task<Prepared> PrepareAmendAsync(string orderId, long? priceE6 = null, long? countE2 = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        if (priceE6 is null && countE2 is null)
        {
            throw new ArgumentException("give a new price, a new count, or both");
        }

        var current = FromRest(await _rest.OrderAsync(orderId, cancellationToken).ConfigureAwait(false));
        if (!current.IsResting)
        {
            throw new OrderNotFoundException($"order {orderId} is {current.Status}, not resting");
        }

        if (countE2 is not null && current.FillE2 > 0)
        {
            throw new ArgumentException("this order has partly filled, and whether Kalshi's count includes the filled part is unverified: change the price only, or cancel and place a new order");
        }

        var total = countE2 ?? (current.FillE2 + current.RemainingE2);
        var remaining = total - current.FillE2;
        if (remaining <= 0)
        {
            throw new ArgumentException("the new count must be more than what has already filled; cancel it");
        }

        // The price is on the YES book, as orders are listed: a bid is a buy of YES, an ask a sell of YES (the same as a buy of NO at 1 - price).
        var yesPrice = priceE6 ?? current.YesPriceE6;
        var request = new TradeRequest(current.Ticker, current.Action, TradeOutcome.Yes, remaining, yesPrice);
        var market = await MarketInfoAsync(current.Ticker, cancellationToken).ConfigureAwait(false);
        var intent = IntentFrom(request, market);
        var (side, sentPrice) = ToV2(TradeOutcome.Yes, current.Action, yesPrice);
        Wire(request, intent, side, sentPrice, "preview").Members();
        var verdict = await VerdictAsync(intent, market, orderId, cancellationToken).ConfigureAwait(false);
        Record("prepare_amend", orderId, verdict, intent);
        return new Prepared(request, market, intent, verdict, side, sentPrice, orderId, total);
    }

    /// <summary>Places the order that was prepared, after re-checking and funding the shard.</summary>
    /// <param name="prepared">The prepared order.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The placement.</returns>
    /// <exception cref="OrderRefusedException">The risk layer refuses it now.</exception>
    /// <exception cref="PlanChangedException">The cost or the funding changed since it was shown.</exception>
    /// <exception cref="WriteOutcomeUnknownException">No clear answer; the client order id is remembered.</exception>
    public async Task<Placement> ExecuteAsync(Prepared prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Replaces is not null)
        {
            throw new ArgumentException("this is an amend: use ExecuteAmendAsync");
        }

        var (approved, funding) = await AuthorizeAsync(prepared, cancellationToken).ConfigureAwait(false);
        var clientOrderId = _newId();
        var wire = Wire(prepared.Request, prepared.Intent, prepared.Side, prepared.PriceE6, clientOrderId);
        try
        {
            var created = await _trading.CreateOrderAsync(wire, cancellationToken).ConfigureAwait(false);
            return new Placement(created, clientOrderId, approved, funding);
        }
        catch (WriteOutcomeUnknownException)
        {
            _tracker.State.NoteUnknownOrder(clientOrderId);
            throw;
        }
    }

    /// <summary>Amends the order that was prepared, after re-checking and funding the shard.</summary>
    /// <param name="prepared">The prepared amend.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The amendment.</returns>
    public async Task<Amendment> ExecuteAmendAsync(Prepared prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Replaces is null || prepared.TotalCountE2 is null)
        {
            throw new ArgumentException("this is not an amend: use ExecuteAsync");
        }

        var (approved, funding) = await AuthorizeAsync(prepared, cancellationToken).ConfigureAwait(false);
        var result = await _trading.AmendOrderAsync(prepared.Replaces, prepared.Request.Ticker, prepared.Side, prepared.PriceE6, prepared.TotalCountE2.Value, prepared.Intent.ExchangeIndex, cancellationToken).ConfigureAwait(false);
        return new Amendment(result, prepared.Replaces, approved, funding);
    }

    /// <summary>Cancels one order; never refused by the risk layer, and works while halted.</summary>
    /// <param name="orderId">Kalshi's order id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    /// <exception cref="OrderNotFoundException">The order is not known to the tracker.</exception>
    public async Task<CancelResult> CancelAsync(string orderId, CancellationToken cancellationToken = default)
    {
        var order = _tracker.Orders.FirstOrDefault(o => o.OrderId == orderId) ?? throw new OrderNotFoundException($"no order {orderId} is known; try refreshing");
        return await _trading.CancelOrderAsync(new CancelTarget(orderId, order.ExchangeIndex, order.Ticker), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Halts (saved first, so nothing new can start), then cancels everything.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>What was done.</returns>
    public async Task<KillReport> KillAsync(string reason = "kill switch", CancellationToken cancellationToken = default)
    {
        _tracker.State.Halt(reason);
        _audit?.Record(_time.GetUtcNow(), "kill", "all", null, reason);
        return await CancelEverythingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels every resting order and looks until none remain; does not halt trading.</summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>What was done.</returns>
    public async Task<KillReport> CancelEverythingAsync(CancellationToken cancellationToken = default)
    {
        var report = new KillReport();
        try
        {
            await _trading.CancelAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Errors.Add($"cancel-all: {ex.GetType().Name}: {ex.Message}");
        }

        var quiet = 0;
        for (var pass = 0; pass < KillPasses; pass++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
            List<Order> resting;
            try
            {
                resting = await _rest.RestingOrdersAsync(cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Errors.Add($"listing orders: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (resting.Count == 0)
            {
                quiet++;
                if (quiet >= 2)
                {
                    report.Errors = [.. report.Errors.Where(e => !e.StartsWith("listing", StringComparison.Ordinal))];   // nothing on two looks, a second apart
                    report.Remaining = [];
                    return report;
                }

                continue;
            }

            quiet = 0;
            var targets = resting.Select(o => new CancelTarget(o.OrderId, o.ExchangeIndex, o.Ticker)).ToList();
            try
            {
                var results = await _trading.CancelOrdersAsync(targets, cancellationToken).ConfigureAwait(false);
                report.Cancelled.AddRange(results.Where(r => r.Error is null).Select(r => r.OrderId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Errors.Add($"cancel: {ex.GetType().Name}: {ex.Message}");
            }

            report.Remaining = [.. resting.Select(o => o.OrderId)];
        }

        report.Errors.Add("orders were still resting after the last pass");
        return report;
    }

    /// <summary>Fires the kill switch if the day's realized loss has reached the limit.</summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns><see langword="true"/> when it fired.</returns>
    public async Task<bool> GuardOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _tracker.Snapshot();
        if (snapshot is null || _tracker.State.Halted)
        {
            return false;
        }

        if (snapshot.RealizedPnlTodayE6 <= -_policy.MaxDailyLossE6)
        {
            await KillAsync("daily loss limit reached", cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Starts checking the daily-loss limit in the background.</summary>
    /// <param name="interval">How often; 5 seconds when omitted.</param>
    public void StartGuard(TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromSeconds(5);
        _guardStop = new CancellationTokenSource();
        var token = _guardStop.Token;
        _guard = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(every, _time, token).ConfigureAwait(false);
                        try
                        {
                            await GuardOnceAsync(token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // a failed check is tried again at the next interval
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // stopped
                }
            },
            CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopGuardAsync().ConfigureAwait(false);

    /// <summary>Stops the background guard; harmless when it is not running.</summary>
    /// <returns>A task that completes when it has stopped.</returns>
    public async Task StopGuardAsync()
    {
        if (_guardStop is null || _guard is null)
        {
            return;
        }

        await _guardStop.CancelAsync().ConfigureAwait(false);
        await _guard.ConfigureAwait(false);
        _guardStop.Dispose();
        _guardStop = null;
        _guard = null;
    }

    /// <summary>Cancels named orders using Kalshi's own record of each (shard and market), so it needs no live tracker and works while halted. One failing id does not stop the others.</summary>
    /// <param name="rest">Market reads.</param>
    /// <param name="trading">The writes.</param>
    /// <param name="orderIds">The orders.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>An outcome per order, in order.</returns>
    public static async Task<IReadOnlyList<CancelOutcome>> CancelByIdsAsync(IMarketRest rest, ITradingApi trading, IReadOnlyList<string> orderIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(trading);
        ArgumentNullException.ThrowIfNull(orderIds);
        var outcomes = new List<CancelOutcome>();
        foreach (var id in orderIds)
        {
            try
            {
                var order = await rest.OrderAsync(id, cancellationToken).ConfigureAwait(false);
                if (order.Status != "resting")
                {
                    outcomes.Add(new CancelOutcome(id, false, $"already {order.Status}"));
                    continue;
                }

                var result = await trading.CancelOrderAsync(new CancelTarget(id, order.ExchangeIndex, order.Ticker), cancellationToken).ConfigureAwait(false);
                outcomes.Add(new CancelOutcome(id, true, $"cancelled {(result.ReducedByE2 is { } by ? RiskChecker.CountText(by) : "?")} left"));
            }
            catch (KalshiApiException ex)
            {
                outcomes.Add(new CancelOutcome(id, false, $"Kalshi said {ex.StatusCode}"));
            }
            catch (WriteOutcomeUnknownException)
            {
                outcomes.Add(new CancelOutcome(id, false, "no clear answer; look at `kterm orders`"));
            }
        }

        return outcomes;
    }

    private static TrackedOrder FromRest(Order order) =>
        new(order.OrderId, order.ClientOrderId, order.Ticker, order.ExchangeIndex, order.Status, order.BookSide, order.YesPriceDollars.Micros, order.InitialCountFp.Hundredths, order.FillCountFp.Hundredths, order.RemainingCountFp.Hundredths, 0);

    private static OrderIntent IntentFrom(TradeRequest request, MarketInfo market) =>
        new(request.Ticker, market.ExchangeIndex, request.Action, request.Outcome, request.CountE2, request.PriceE6, market.FeeMultiplierE6);

    private static OrderRequest Wire(TradeRequest request, OrderIntent intent, BookSide side, long yesPriceE6, string clientOrderId) =>
        new(request.Ticker, intent.ExchangeIndex, side, intent.CountE2, yesPriceE6, clientOrderId, request.TimeInForce, PostOnly: request.PostOnly);

    private void Record(string what, string reference, Verdict verdict, OrderIntent intent) =>
        _audit?.Record(_time.GetUtcNow(), "risk." + what, reference, null, (verdict.Ok ? "approved" : "refused: " + string.Join(",", verdict.Refusals.Select(r => RiskCodes.Name(r.Code)))) + " | " + Summary(intent, verdict));

    /// <summary>What the audit log keeps of an order: the side, count and price, the worst-case cost and the funding. No secret is in any of it.</summary>
    private static string Summary(OrderIntent intent, Verdict verdict)
    {
        var text = $"{intent.Action.ToString().ToUpperInvariant()} {RiskChecker.CountText(intent.CountE2)} {intent.Outcome.ToString().ToUpperInvariant()} at {RiskChecker.DollarsText(intent.PriceE6)}";
        if (verdict.WorstCaseCostE6 is { } cost)
        {
            text += $"; worst-case {RiskChecker.DollarsText(cost)}";
        }

        return verdict.Funding.Count == 0 ? text : text + "; funding " + string.Join(", ", verdict.Funding.Select(t => $"{RiskChecker.DollarsText(t.AmountE6)} shard {t.Source} to {t.Destination}"));
    }

    private async Task<Verdict> VerdictAsync(OrderIntent intent, MarketInfo market, string? replaces, CancellationToken cancellationToken)
    {
        await _tracker.RefreshBalanceAsync(cancellationToken).ConfigureAwait(false);
        var verdict = RiskChecker.Check(intent, _tracker.Snapshot(replaces), _policy, _time.GetUtcNow(), _production);
        if (market.Tradable)
        {
            return verdict;
        }

        return verdict with { Approved = null, Refusals = [.. verdict.Refusals, new Refusal(RiskCode.MarketNotTradable, $"{market.Ticker} is {market.Status}")], Funding = [] };
    }

    /// <summary>Re-checks with fresh data and funds the shard. Returns the proof and the transfers made.</summary>
    private async Task<(ApprovedOrder Approved, IReadOnlyList<Transfer> Funding)> AuthorizeAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        if (!prepared.Verdict.Ok)
        {
            throw new OrderRefusedException(prepared.Verdict);
        }

        var fresh = await VerdictAsync(prepared.Intent, prepared.Market, prepared.Replaces, cancellationToken).ConfigureAwait(false);
        Record("execute", prepared.Replaces ?? prepared.Request.Ticker, fresh, prepared.Intent);
        if (fresh.TripKill && !_tracker.State.Halted)
        {
            await KillAsync("daily loss limit reached", cancellationToken).ConfigureAwait(false);
        }

        if (!fresh.Ok)
        {
            throw new OrderRefusedException(fresh);
        }

        if (!fresh.Funding.SequenceEqual(prepared.Verdict.Funding) || (fresh.WorstCaseCostE6 ?? 0) > (prepared.Verdict.WorstCaseCostE6 ?? 0))
        {
            throw new PlanChangedException("the order's cost or funding changed since it was shown; prepare it again");
        }

        foreach (var transfer in fresh.Funding)
        {
            await MoveAsync(transfer, cancellationToken).ConfigureAwait(false);
        }

        var approved = fresh.Approved!;
        if (fresh.Funding.Count > 0)
        {
            approved = await AwaitFundingAsync(prepared, cancellationToken).ConfigureAwait(false);
        }

        return (approved, fresh.Funding);
    }

    /// <summary>
    /// Waits for the transfers to show in the balance, then checks once more. Kalshi's balance lags a transfer by a moment (seen live: the very next read still showed the old shard balance). An order is refused at once for any
    /// real reason (a halt, a limit); only "the shard still needs funding" is waited on, for about ten seconds.
    /// </summary>
    private async Task<ApprovedOrder> AwaitFundingAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < FundingPolls; poll++)
        {
            var after = await VerdictAsync(prepared.Intent, prepared.Market, prepared.Replaces, cancellationToken).ConfigureAwait(false);
            if (!after.Ok)
            {
                throw new FundingShortException("the order is no longer allowed after the transfers: " + string.Join("; ", after.Refusals.Select(r => r.Message)));
            }

            if (after.Funding.Count == 0)
            {
                return after.Approved!;
            }

            await Task.Delay(FundingPoll, _time, cancellationToken).ConfigureAwait(false);
        }

        throw new FundingShortException($"the transfer has not shown up in the shard balance after {FundingPolls * FundingPoll.TotalSeconds:0} seconds. It is recorded as made: check `kterm balance`, then run the order again.");
    }

    private async Task MoveAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        var id = _newId();
        var state = _tracker.State;
        state.BeginTransfer(id, transfer.Source, transfer.Destination, transfer.AmountE6);   // counted and saved before the request is sent
        try
        {
            await _trading.TransferAsync(transfer.Source, transfer.Destination, transfer.AmountE6, id, cancellationToken).ConfigureAwait(false);
        }
        catch (KalshiApiException ex)
        {
            state.AbortTransfer(id);
            throw new TransferFailedException($"Kalshi refused the shard transfer: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TransferUnknownException($"no clear answer to the shard transfer {id} ({ex.GetType().Name}); it stays recorded as pending. Check the balance before trying again.", ex);
        }

        state.FinishTransfer(id);
    }
}
