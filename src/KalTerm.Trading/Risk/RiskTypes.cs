namespace KalTerm.Trading.Risk;

/// <summary>Whether an order buys or sells.</summary>
public enum TradeAction
{
    /// <summary>Buy contracts of the outcome.</summary>
    Buy,

    /// <summary>Sell contracts of the outcome.</summary>
    Sell,
}

/// <summary>Which outcome of a market an order trades.</summary>
public enum TradeOutcome
{
    /// <summary>The YES contract.</summary>
    Yes,

    /// <summary>The NO contract.</summary>
    No,
}

/// <summary>Why an order was refused. The wire names (snake case) are what logs and the audit file carry.</summary>
public enum RiskCode
{
    /// <summary>The risk file does not allow orders on the production account.</summary>
    ProductionDisabled,

    /// <summary>There is no account snapshot yet.</summary>
    NoSnapshot,

    /// <summary>The snapshot is older than the limit, or dated in the future.</summary>
    StaleSnapshot,

    /// <summary>The private account stream is down, so positions may be stale.</summary>
    StreamDown,

    /// <summary>The order's exchange shard is not in the snapshot.</summary>
    UnknownShard,

    /// <summary>Trading is not active on the order's shard.</summary>
    ShardNotTrading,

    /// <summary>The fee cannot be modelled, so the cost is unknown.</summary>
    UnknownFee,

    /// <summary>More contracts than one order may have.</summary>
    OrderTooLarge,

    /// <summary>The worst-case cost is above the per-order limit.</summary>
    NotionalTooLarge,

    /// <summary>The order could leave more contracts in the market than allowed.</summary>
    PositionLimit,

    /// <summary>Too many orders are already open.</summary>
    TooManyOrders,

    /// <summary>The account's exposure would pass its limit.</summary>
    ExposureLimit,

    /// <summary>Today's realized loss has reached the daily limit.</summary>
    DailyLoss,

    /// <summary>The account cannot put the cost on the order's shard.</summary>
    InsufficientFunds,

    /// <summary>The funding transfer would pass a per-order or per-day cap.</summary>
    TransferLimit,

    /// <summary>The kill switch fired and has not been cleared.</summary>
    Halted,

    /// <summary>The market cannot be traded.</summary>
    MarketNotTradable,
}

/// <summary>Names for <see cref="RiskCode"/>.</summary>
public static class RiskCodes
{
    /// <summary>The snake-case name of a code, for logs and the audit file.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The name, for example <c>order_too_large</c>.</returns>
    public static string Name(RiskCode code) => code switch
    {
        RiskCode.ProductionDisabled => "production_disabled",
        RiskCode.NoSnapshot => "no_snapshot",
        RiskCode.StaleSnapshot => "stale_snapshot",
        RiskCode.StreamDown => "stream_down",
        RiskCode.UnknownShard => "unknown_shard",
        RiskCode.ShardNotTrading => "shard_not_trading",
        RiskCode.UnknownFee => "unknown_fee",
        RiskCode.OrderTooLarge => "order_too_large",
        RiskCode.NotionalTooLarge => "notional_too_large",
        RiskCode.PositionLimit => "position_limit",
        RiskCode.TooManyOrders => "too_many_orders",
        RiskCode.ExposureLimit => "exposure_limit",
        RiskCode.DailyLoss => "daily_loss",
        RiskCode.InsufficientFunds => "insufficient_funds",
        RiskCode.TransferLimit => "transfer_limit",
        RiskCode.Halted => "halted",
        RiskCode.MarketNotTradable => "market_not_tradable",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}

/// <summary>One reason an order was refused.</summary>
/// <param name="Code">The reason.</param>
/// <param name="Message">What to tell the person.</param>
public sealed record Refusal(RiskCode Code, string Message);

/// <summary>What the person wants: the outcome they trade and its limit price.</summary>
public sealed record OrderIntent
{
    /// <summary>Creates an intent.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="exchangeIndex">The exchange shard of the market.</param>
    /// <param name="action">Buy or sell.</param>
    /// <param name="outcome">The outcome traded.</param>
    /// <param name="countE2">Contracts, in hundredths; positive.</param>
    /// <param name="priceE6">The limit price in millionths of a dollar, strictly between $0 and $1.</param>
    /// <param name="feeMultiplierE6">The series' fee multiplier in millionths, or <see langword="null"/> when the fee cannot be modelled.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count or the price is out of range: a programming error, not a verdict.</exception>
    public OrderIntent(string ticker, int exchangeIndex, TradeAction action, TradeOutcome outcome, long countE2, long priceE6, long? feeMultiplierE6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countE2);
        if (priceE6 is <= 0 or >= 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priceE6), priceE6, "the price must be strictly between $0 and $1");
        }

        Ticker = ticker;
        ExchangeIndex = exchangeIndex;
        Action = action;
        Outcome = outcome;
        CountE2 = countE2;
        PriceE6 = priceE6;
        FeeMultiplierE6 = feeMultiplierE6;
    }

    /// <summary>The market.</summary>
    public string Ticker { get; }

    /// <summary>The exchange shard of the market.</summary>
    public int ExchangeIndex { get; }

    /// <summary>Buy or sell.</summary>
    public TradeAction Action { get; }

    /// <summary>The outcome traded.</summary>
    public TradeOutcome Outcome { get; }

    /// <summary>Contracts, in hundredths.</summary>
    public long CountE2 { get; }

    /// <summary>The limit price, in millionths of a dollar.</summary>
    public long PriceE6 { get; }

    /// <summary>The fee multiplier in millionths, or <see langword="null"/>.</summary>
    public long? FeeMultiplierE6 { get; }

    /// <summary>Whether filling it moves the net position towards YES (otherwise towards NO).</summary>
    public bool AddsYes => (Action, Outcome) is (TradeAction.Buy, TradeOutcome.Yes) or (TradeAction.Sell, TradeOutcome.No);
}

/// <summary>One exchange shard of the account.</summary>
/// <param name="Index">The shard.</param>
/// <param name="BalanceE6">What is free to use on it, in millionths of a dollar.</param>
/// <param name="TradingActive">Whether Kalshi lets orders trade on it.</param>
/// <param name="TransfersActive">Whether Kalshi lets collateral move to or from it.</param>
public sealed record ShardState(int Index, long BalanceE6, bool TradingActive, bool TransfersActive);

/// <summary>What the account holds in one market. The resting figures are open orders that would add to the YES (or NO) position if they filled.</summary>
/// <param name="PositionE2">The net YES position in hundredths (negative: long NO).</param>
/// <param name="RestingYesE2">Open orders that would add to YES.</param>
/// <param name="RestingNoE2">Open orders that would add to NO.</param>
public sealed record MarketState(long PositionE2 = 0, long RestingYesE2 = 0, long RestingNoE2 = 0);

/// <summary>The picture of the account that limits are checked against.</summary>
/// <param name="AsOf">When the picture was taken.</param>
/// <param name="Shards">The shards by index.</param>
/// <param name="Markets">Positions and resting orders by ticker; a market not listed has none.</param>
/// <param name="OpenOrders">How many orders are open.</param>
/// <param name="ExposureE6">Market exposure plus the cost of resting buys, account-wide.</param>
/// <param name="RealizedPnlTodayE6">Today's realized profit (negative: loss).</param>
/// <param name="TransferredTodayE6">How much has been moved between shards today.</param>
/// <param name="StreamOk">Whether the private account stream is up.</param>
/// <param name="Halted">Whether the kill switch fired; nothing new is allowed until it is cleared.</param>
public sealed record AccountSnapshot(
    DateTimeOffset AsOf,
    IReadOnlyDictionary<int, ShardState> Shards,
    IReadOnlyDictionary<string, MarketState> Markets,
    int OpenOrders,
    long ExposureE6,
    long RealizedPnlTodayE6,
    long TransferredTodayE6,
    bool StreamOk,
    bool Halted = false);

/// <summary>The limits. The defaults are deliberately tiny; they change only by editing the risk file.</summary>
public sealed record RiskPolicy
{
    /// <summary>Most contracts in one order, in hundredths. Default 5.</summary>
    public long MaxOrderCountE2 { get; init; } = 500;

    /// <summary>Most contracts one market may hold, resting orders included, in hundredths. Default 10.</summary>
    public long MaxPositionE2 { get; init; } = 1_000;

    /// <summary>Most cash one order can take, fee included, in millionths of a dollar. Default $1.</summary>
    public long MaxOrderNotionalE6 { get; init; } = 1_000_000;

    /// <summary>Most money at risk across the account, in millionths of a dollar. Default $3.</summary>
    public long MaxOpenExposureE6 { get; init; } = 3_000_000;

    /// <summary>The realized loss in a day at which trading stops. Default $2.</summary>
    public long MaxDailyLossE6 { get; init; } = 2_000_000;

    /// <summary>Most open orders. Default 3.</summary>
    public int MaxOpenOrders { get; init; } = 3;

    /// <summary>Most one order's funding may move between shards. Default $2.</summary>
    public long MaxTransferPerOrderE6 { get; init; } = 2_000_000;

    /// <summary>Most that may be moved between shards in a day. Default $5.</summary>
    public long MaxTransferPerDayE6 { get; init; } = 5_000_000;

    /// <summary>How old the account snapshot may be, in seconds. Default 10.</summary>
    public int SnapshotMaxAgeSeconds { get; init; } = 10;

    /// <summary>Whether orders may be sent on the production account. Default false.</summary>
    public bool ProductionEnabled { get; init; }
}

/// <summary>Collateral to move from one shard to another before an order.</summary>
/// <param name="Source">The shard it comes from.</param>
/// <param name="Destination">The shard that needs it.</param>
/// <param name="AmountE6">The amount, in whole centicents ($0.0001), in millionths of a dollar.</param>
public sealed record Transfer(int Source, int Destination, long AmountE6);

/// <summary>
/// Proof that <see cref="RiskChecker.Check"/> approved an order. It has no public constructor, and only the risk checker calls the internal one (a test scans the sources to keep it so), so the order service
/// that accepts nothing else cannot be handed an order that skipped the checks.
/// </summary>
public sealed class ApprovedOrder
{
    internal ApprovedOrder(OrderIntent intent, long feeE6, long worstCaseCostE6, IReadOnlyList<Transfer> funding, bool production, DateTimeOffset approvedAt)
    {
        Intent = intent;
        FeeE6 = feeE6;
        WorstCaseCostE6 = worstCaseCostE6;
        Funding = funding;
        Production = production;
        ApprovedAt = approvedAt;
    }

    /// <summary>What was approved.</summary>
    public OrderIntent Intent { get; }

    /// <summary>The worst-case fee.</summary>
    public long FeeE6 { get; }

    /// <summary>The most the order can take from the balance, fee included.</summary>
    public long WorstCaseCostE6 { get; }

    /// <summary>The transfers to make first (empty when the shard already has the money).</summary>
    public IReadOnlyList<Transfer> Funding { get; }

    /// <summary>Whether it is for the production account.</summary>
    public bool Production { get; }

    /// <summary>When it was approved.</summary>
    public DateTimeOffset ApprovedAt { get; }
}

/// <summary>What the risk layer decided about one order.</summary>
/// <param name="Approved">The approved order, or <see langword="null"/> when it was refused.</param>
/// <param name="Refusals">Every reason it was refused (all rules run, so the person sees all of them at once).</param>
/// <param name="FeeE6">The worst-case fee, or <see langword="null"/> when it cannot be modelled.</param>
/// <param name="WorstCaseCostE6">The worst-case cost, or <see langword="null"/>.</param>
/// <param name="Funding">The transfers that would be needed (empty for a refusal that is not about money).</param>
/// <param name="TripKill">Whether the daily loss limit has been reached, so the caller fires the kill switch.</param>
public sealed record Verdict(ApprovedOrder? Approved, IReadOnlyList<Refusal> Refusals, long? FeeE6, long? WorstCaseCostE6, IReadOnlyList<Transfer> Funding, bool TripKill = false)
{
    /// <summary>Whether the order was approved.</summary>
    public bool Ok => Approved is not null;
}
