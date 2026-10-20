using System.Globalization;
using KalTerm.Trading.Risk;

namespace KalTerm.Trading.Transport;

/// <summary>The side of the YES book (Create Order V2 has no yes or no field).</summary>
public enum BookSide
{
    /// <summary>A bid: buys YES.</summary>
    Bid,

    /// <summary>An ask: sells YES.</summary>
    Ask,
}

/// <summary>How long an order lives.</summary>
public enum TimeInForce
{
    /// <summary>Rests until filled or cancelled (or its expiration).</summary>
    GoodTillCanceled,

    /// <summary>Fills what it can now, cancels the rest.</summary>
    ImmediateOrCancel,

    /// <summary>Fills completely now or not at all.</summary>
    FillOrKill,
}

/// <summary>What happens when an order would trade with the account's own resting order.</summary>
public enum SelfTradePrevention
{
    /// <summary>The incoming order is cancelled at the cross.</summary>
    TakerAtCross,

    /// <summary>The resting order is cancelled.</summary>
    Maker,
}

/// <summary>Fixed-point text for Kalshi's bodies, built from integers so no float or decimal rounding is possible.</summary>
public static class FixedPointText
{
    /// <summary>Hundredths as text with two decimals.</summary>
    /// <param name="hundredths">Not negative.</param>
    /// <param name="what">What it is, for the message.</param>
    /// <returns>The text, for example <c>2.50</c>.</returns>
    public static string Count(long hundredths, string what = "count")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hundredths, what);
        return string.Create(CultureInfo.InvariantCulture, $"{hundredths / 100}.{hundredths % 100:D2}");
    }

    /// <summary>Millionths of a dollar as text with four decimals; a finer price is refused, never rounded.</summary>
    /// <param name="micros">Not negative, a whole number of hundred-microdollars.</param>
    /// <param name="what">What it is, for the message.</param>
    /// <returns>The text, for example <c>0.0100</c>.</returns>
    public static string Price(long micros, string what = "price")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros, what);
        if (micros % 100 != 0)
        {
            throw new ArgumentException($"{what} has more than 4 decimal places", what);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{micros / 1_000_000}.{micros % 1_000_000 / 100:D4}");
    }
}

/// <summary>A new order as Kalshi takes it. Build one from an approved order with <see cref="From"/>.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="ExchangeIndex">The exchange shard.</param>
/// <param name="Side">The side of the YES book.</param>
/// <param name="CountE2">Contracts in hundredths; positive.</param>
/// <param name="PriceE6">The YES price in millionths, strictly between $0 and $1, at most four decimals.</param>
/// <param name="ClientOrderId">Required: how a lost answer is resolved.</param>
/// <param name="TimeInForce">How long it lives.</param>
/// <param name="SelfTradePrevention">The self-trade rule.</param>
/// <param name="PostOnly">Never take liquidity.</param>
/// <param name="ReduceOnly">Only reduce a position (immediate-or-cancel only).</param>
/// <param name="Expiration">When a good-till-canceled order lapses.</param>
public sealed record OrderRequest(
    string Ticker,
    int ExchangeIndex,
    BookSide Side,
    long CountE2,
    long PriceE6,
    string ClientOrderId,
    TimeInForce TimeInForce = TimeInForce.GoodTillCanceled,
    SelfTradePrevention SelfTradePrevention = SelfTradePrevention.TakerAtCross,
    bool PostOnly = false,
    bool ReduceOnly = false,
    DateTimeOffset? Expiration = null)
{
    /// <summary>
    /// The order for an approved intent. Everything is on the YES book: buying YES is a bid, selling YES an ask, buying NO an ask at the complement of the price, selling NO a bid at it.
    /// </summary>
    /// <param name="approved">The proof from the risk layer.</param>
    /// <param name="clientOrderId">The id to send; <see cref="NewClientOrderId"/> makes one.</param>
    /// <param name="timeInForce">How long it lives.</param>
    /// <returns>The order to send.</returns>
    public static OrderRequest From(ApprovedOrder approved, string clientOrderId, TimeInForce timeInForce = TimeInForce.GoodTillCanceled)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var intent = approved.Intent;
        var yes = intent.Outcome == TradeOutcome.Yes;
        var buy = intent.Action == TradeAction.Buy;
        var side = yes == buy ? BookSide.Bid : BookSide.Ask;
        var price = yes ? intent.PriceE6 : 1_000_000 - intent.PriceE6;
        return new OrderRequest(intent.Ticker, intent.ExchangeIndex, side, intent.CountE2, price, clientOrderId, timeInForce);
    }

    /// <summary>A fresh client order id (a UUID, the form Kalshi accepts and the order lookup matches on).</summary>
    /// <returns>The id.</returns>
    public static string NewClientOrderId() => Guid.NewGuid().ToString("D");

    /// <summary>Checks the order and returns the JSON members Kalshi expects, in order.</summary>
    /// <returns>The members.</returns>
    /// <exception cref="ArgumentException">The order is malformed.</exception>
    internal IReadOnlyList<KeyValuePair<string, object>> Members()
    {
        if (string.IsNullOrWhiteSpace(ClientOrderId))
        {
            throw new ArgumentException("client_order_id is required: it is how a lost answer is resolved", nameof(ClientOrderId));
        }

        if (string.IsNullOrWhiteSpace(Ticker))
        {
            throw new ArgumentException("the ticker is required", nameof(Ticker));
        }

        if (CountE2 <= 0)
        {
            throw new ArgumentException("the count must be positive", nameof(CountE2));
        }

        if (PriceE6 is <= 0 or >= 1_000_000)
        {
            throw new ArgumentException("the price must be strictly between $0 and $1", nameof(PriceE6));
        }

        if (ReduceOnly && TimeInForce != TimeInForce.ImmediateOrCancel)
        {
            throw new ArgumentException("reduce_only needs immediate_or_cancel", nameof(ReduceOnly));
        }

        if (Expiration is not null && TimeInForce != TimeInForce.GoodTillCanceled)
        {
            throw new ArgumentException("an expiration is for good_till_canceled orders only", nameof(Expiration));
        }

        var members = new List<KeyValuePair<string, object>>
        {
            new("ticker", Ticker),
            new("client_order_id", ClientOrderId),
            new("side", Side == BookSide.Bid ? "bid" : "ask"),
            new("count", FixedPointText.Count(CountE2)),
            new("price", FixedPointText.Price(PriceE6)),
            new("time_in_force", TimeInForce switch
            {
                TimeInForce.GoodTillCanceled => "good_till_canceled",
                TimeInForce.ImmediateOrCancel => "immediate_or_cancel",
                _ => "fill_or_kill",
            }),
            new("self_trade_prevention_type", SelfTradePrevention == SelfTradePrevention.Maker ? "maker" : "taker_at_cross"),
            new("exchange_index", ExchangeIndex),
        };
        if (PostOnly)
        {
            members.Add(new("post_only", true));
        }

        if (ReduceOnly)
        {
            members.Add(new("reduce_only", true));
        }

        if (Expiration is { } at)
        {
            members.Add(new("expiration_time", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        return members;
    }
}

/// <summary>The reply to a new order.</summary>
/// <param name="OrderId">Kalshi's id.</param>
/// <param name="ClientOrderId">Our id, echoed.</param>
/// <param name="FillCountE2">Filled so far, in hundredths.</param>
/// <param name="RemainingCountE2">Still resting, in hundredths.</param>
/// <param name="Recovered">Found by looking after an unclear answer, not read from the reply.</param>
public sealed record CreatedOrder(string OrderId, string ClientOrderId, long FillCountE2, long RemainingCountE2, bool Recovered = false);

/// <summary>The reply to an amend or a decrease.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="RemainingCountE2">Still resting in hundredths, when Kalshi said.</param>
public sealed record ChangedOrder(string OrderId, long? RemainingCountE2);

/// <summary>An order to cancel: the shard and the market are part of the request.</summary>
/// <param name="OrderId">Kalshi's id.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="MarketTicker">The market.</param>
public sealed record CancelTarget(string OrderId, int ExchangeIndex, string MarketTicker);

/// <summary>The outcome of cancelling one order.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="ReducedByE2">Contracts taken off the book, when Kalshi said.</param>
/// <param name="AlreadyGone">A retry found the order gone, so an earlier attempt did it.</param>
/// <param name="Error">Kalshi's refusal of this order in a batch, or <see langword="null"/>.</param>
public sealed record CancelResult(string OrderId, long? ReducedByE2, bool AlreadyGone = false, string? Error = null);
