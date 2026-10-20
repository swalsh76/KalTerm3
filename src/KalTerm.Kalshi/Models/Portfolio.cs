using System.Text.Json.Serialization;
using KalTerm.Core.Money;

namespace KalTerm.Kalshi.Models;

// Typed models for the account (portfolio) endpoints. Read side only. The field sets are the ones measured on a live account (BUILD-LOG 8.1 and the Python reference's fixtures): money is a fixed-point
// dollar string (4 or 6 decimals) or, on a few older fields, integer cents; counts are fixed-point strings with two decimals; everything is parsed exactly, never through a float. Kalshi keeps every order
// on the YES book, so Side is always "yes"; the person's intent is Action (buy or sell) with OutcomeSide (yes or no). Unknown fields are ignored, because Kalshi adds fields over time.

/// <summary>One exchange shard's share of the balance.</summary>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="Balance">Dollars available on it.</param>
public sealed record ShardBalance(int ExchangeIndex, Dollars Balance);

/// <summary>
/// The account balance. <c>Balance</c> is <c>BalanceDollars</c> truncated to whole cents ($10.3490 becomes 1034): always use the dollar value, the cents field silently loses up to 0.99 of a cent.
/// </summary>
/// <param name="Balance">Available balance in cents (truncated; prefer <paramref name="BalanceDollars"/>).</param>
/// <param name="BalanceDollars">Available balance in dollars, exact.</param>
/// <param name="PortfolioValue">Portfolio value in cents.</param>
/// <param name="UpdatedTs">When the balance last changed, in Unix seconds.</param>
/// <param name="BalanceBreakdown">One row per exchange shard; empty for a key that is restricted to a subaccount.</param>
public sealed record AccountBalance(
    long Balance,
    Dollars BalanceDollars,
    long PortfolioValue = 0,
    long? UpdatedTs = null,
    IReadOnlyList<ShardBalance>? BalanceBreakdown = null)
{
    /// <summary>Dollars available on one exchange shard (zero when it is not listed).</summary>
    /// <param name="exchangeIndex">The shard.</param>
    /// <returns>The balance.</returns>
    public Dollars Shard(int exchangeIndex) => BalanceBreakdown?.FirstOrDefault(row => row.ExchangeIndex == exchangeIndex)?.Balance ?? Dollars.Zero;
}

/// <summary>A position in one market.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="PositionFp">Net YES contracts; negative means long NO.</param>
/// <param name="TotalTradedDollars">Total traded.</param>
/// <param name="MarketExposureDollars">Money at risk in the market.</param>
/// <param name="RealizedPnlDollars">Locked-in profit and loss.</param>
/// <param name="FeesPaidDollars">Fees paid on fills.</param>
/// <param name="LastUpdatedTs">When the position last changed.</param>
public sealed record MarketPosition(
    string Ticker,
    int ExchangeIndex = 0,
    Contracts PositionFp = default,
    Dollars? TotalTradedDollars = null,
    Dollars? MarketExposureDollars = null,
    Dollars? RealizedPnlDollars = null,
    Dollars? FeesPaidDollars = null,
    DateTimeOffset? LastUpdatedTs = null);

/// <summary>A position across an event.</summary>
/// <param name="EventTicker">The event.</param>
/// <param name="TotalCostDollars">Total cost.</param>
/// <param name="TotalCostSharesFp">Shares, both YES and NO.</param>
/// <param name="EventExposureDollars">Money at risk in the event.</param>
/// <param name="RealizedPnlDollars">Locked-in profit and loss.</param>
/// <param name="FeesPaidDollars">Fees paid on fills.</param>
public sealed record EventPosition(
    string EventTicker,
    Dollars? TotalCostDollars = null,
    Contracts? TotalCostSharesFp = null,
    Dollars? EventExposureDollars = null,
    Dollars? RealizedPnlDollars = null,
    Dollars? FeesPaidDollars = null);

/// <summary>One page of positions; the cursor is empty on the last page.</summary>
/// <param name="MarketPositions">Positions by market.</param>
/// <param name="EventPositions">Positions by event.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record PositionsPage(IReadOnlyList<MarketPosition>? MarketPositions = null, IReadOnlyList<EventPosition>? EventPositions = null, string Cursor = "");

/// <summary>An order.</summary>
/// <param name="OrderId">Kalshi's order id.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Status">resting, canceled, executed, ...</param>
/// <param name="Action">buy or sell.</param>
/// <param name="OutcomeSide">yes or no: the outcome the person trades.</param>
/// <param name="YesPriceDollars">The limit on the YES book.</param>
/// <param name="NoPriceDollars">The same limit as a NO price.</param>
/// <param name="InitialCountFp">Contracts ordered.</param>
/// <param name="FillCountFp">Contracts filled.</param>
/// <param name="RemainingCountFp">Contracts still resting.</param>
/// <param name="ClientOrderId">The id the client gave it (how a lost answer is resolved).</param>
/// <param name="UserId">The account's user id.</param>
/// <param name="Type">For example <c>limit</c>.</param>
/// <param name="Side">The book it sits on (always yes).</param>
/// <param name="BookSide">bid or ask.</param>
/// <param name="TakerFeesDollars">Fees paid as taker.</param>
/// <param name="MakerFeesDollars">Fees paid as maker.</param>
/// <param name="TakerFillCostDollars">Cost of taker fills.</param>
/// <param name="MakerFillCostDollars">Cost of maker fills.</param>
/// <param name="SelfTradePreventionType">taker_at_cross or maker.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="SubaccountNumber">The subaccount (0 is the primary).</param>
/// <param name="CreatedTime">When it was placed.</param>
/// <param name="LastUpdateTime">When it last changed.</param>
/// <param name="ExpirationTime">When it expires, if it does.</param>
public sealed record Order(
    string OrderId,
    string Ticker,
    string Status,
    string Action,
    string OutcomeSide,
    Dollars YesPriceDollars,
    Dollars NoPriceDollars,
    Contracts InitialCountFp,
    Contracts FillCountFp,
    Contracts RemainingCountFp,
    string ClientOrderId = "",
    string UserId = "",
    string Type = "limit",
    string Side = "yes",
    string BookSide = "",
    Dollars TakerFeesDollars = default,
    Dollars MakerFeesDollars = default,
    Dollars TakerFillCostDollars = default,
    Dollars MakerFillCostDollars = default,
    string SelfTradePreventionType = "",
    int ExchangeIndex = 0,
    int SubaccountNumber = 0,
    DateTimeOffset? CreatedTime = null,
    DateTimeOffset? LastUpdateTime = null,
    DateTimeOffset? ExpirationTime = null)
{
    /// <summary>Whether the order is still resting on the book.</summary>
    public bool IsResting => Status == "resting";
}

/// <summary>One page of orders; the cursor is empty on the last page.</summary>
/// <param name="Orders">The orders.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record OrdersPage(IReadOnlyList<Order>? Orders = null, string Cursor = "");

/// <summary>The reply to a request for one order.</summary>
/// <param name="Order">The order.</param>
public sealed record OrderEnvelope(Order Order);

/// <summary>A fill: part of an order that traded.</summary>
/// <param name="FillId">The fill.</param>
/// <param name="OrderId">The order it belongs to.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Action">buy or sell.</param>
/// <param name="OutcomeSide">yes or no.</param>
/// <param name="CountFp">Contracts filled.</param>
/// <param name="YesPriceDollars">The fill price as a YES price.</param>
/// <param name="NoPriceDollars">The fill price as a NO price.</param>
/// <param name="TradeId">The trade it was part of.</param>
/// <param name="MarketTicker">The market (repeated by Kalshi).</param>
/// <param name="Side">The book (always yes).</param>
/// <param name="BookSide">bid or ask.</param>
/// <param name="FeeCost">The fee in dollars.</param>
/// <param name="IsTaker">Whether the order took liquidity.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="SubaccountNumber">The subaccount.</param>
/// <param name="CreatedTime">When it happened.</param>
/// <param name="Ts">When it happened, in Unix seconds.</param>
public sealed record Fill(
    string FillId,
    string OrderId,
    string Ticker,
    string Action,
    string OutcomeSide,
    Contracts CountFp,
    Dollars YesPriceDollars,
    Dollars NoPriceDollars,
    string TradeId = "",
    string MarketTicker = "",
    string Side = "yes",
    string BookSide = "",
    Dollars FeeCost = default,
    bool IsTaker = false,
    int ExchangeIndex = 0,
    int SubaccountNumber = 0,
    DateTimeOffset? CreatedTime = null,
    long? Ts = null);

/// <summary>One page of fills; the cursor is empty on the last page.</summary>
/// <param name="Fills">The fills.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record FillsPage(IReadOnlyList<Fill>? Fills = null, string Cursor = "");

/// <summary>What a market paid out when it settled.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="EventTicker">The event.</param>
/// <param name="MarketResult">yes, no, ...</param>
/// <param name="YesCountFp">YES contracts held at settlement.</param>
/// <param name="NoCountFp">NO contracts held at settlement.</param>
/// <param name="YesTotalCostDollars">What the YES contracts cost.</param>
/// <param name="NoTotalCostDollars">What the NO contracts cost.</param>
/// <param name="FeeCost">The fee in dollars.</param>
/// <param name="Revenue">The payout in cents.</param>
/// <param name="Value">Cents per winning contract.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="SettledTime">When it settled.</param>
public sealed record Settlement(
    string Ticker,
    string EventTicker = "",
    string MarketResult = "",
    Contracts YesCountFp = default,
    Contracts NoCountFp = default,
    Dollars YesTotalCostDollars = default,
    Dollars NoTotalCostDollars = default,
    Dollars FeeCost = default,
    long Revenue = 0,
    long? Value = null,
    int ExchangeIndex = 0,
    DateTimeOffset? SettledTime = null);

/// <summary>One page of settlements; the cursor is empty on the last page.</summary>
/// <param name="Settlements">The settlements.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record SettlementsPage(IReadOnlyList<Settlement>? Settlements = null, string Cursor = "");

/// <summary>Where a resting order stands in the queue.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="MarketTicker">The market.</param>
/// <param name="QueuePositionFp">Contracts ahead of it.</param>
public sealed record QueuePosition(string OrderId, string MarketTicker, Contracts QueuePositionFp);

/// <summary>The reply to a request for queue positions.</summary>
/// <param name="Positions">The positions.</param>
public sealed record QueuePositionsReply([property: JsonPropertyName("queue_positions")] IReadOnlyList<QueuePosition>? Positions = null);
