using KalTerm.Trading.Risk;

namespace KalTerm.Terminal.Trading;

/// <summary>Whether the terminal has an open trading session with Kalshi.</summary>
internal enum LinkState
{
    /// <summary>Nothing opened yet.</summary>
    NotConnected,

    /// <summary>Opening: the key is read, the account is read and followed.</summary>
    Connecting,

    /// <summary>Open.</summary>
    Connected,

    /// <summary>Opening failed; the reason is shown and Connect tries again.</summary>
    Failed,
}

/// <summary>One exchange shard of the account.</summary>
/// <param name="Index">The shard.</param>
/// <param name="BalanceE6">Free balance, in millionths of a dollar.</param>
/// <param name="TradingActive">Whether Kalshi lets orders trade on it.</param>
internal sealed record ShardView(int Index, long BalanceE6, bool TradingActive);

/// <summary>A resting order, as Kalshi's YES book holds it.</summary>
/// <param name="OrderId">Kalshi's id.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Shard">The exchange shard.</param>
/// <param name="BookSide">bid (buys YES, or sells NO) or ask (sells YES, or buys NO).</param>
/// <param name="YesPriceE6">The YES price, in millionths.</param>
/// <param name="InitialE2">Contracts when placed, in hundredths.</param>
/// <param name="FilledE2">Contracts filled.</param>
/// <param name="RemainingE2">Contracts still resting.</param>
/// <param name="Status">resting, canceled, executed or unknown.</param>
internal sealed record OrderView(string OrderId, string Ticker, int Shard, string BookSide, long YesPriceE6, long InitialE2, long FilledE2, long RemainingE2, string Status);

/// <summary>What the account holds in a market.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="PositionE2">The net YES position in hundredths (negative: long NO).</param>
/// <param name="ExposureE6">Money at risk in it.</param>
/// <param name="RealizedE6">Cumulative realized profit.</param>
internal sealed record PositionView(string Ticker, long PositionE2, long ExposureE6, long RealizedE6);

/// <summary>A fill.</summary>
/// <param name="Key">The trade's id, so a fill seen twice is shown once.</param>
/// <param name="Time">When, or <see langword="null"/> when Kalshi did not say.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Action">buy or sell.</param>
/// <param name="Side">yes or no.</param>
/// <param name="YesPriceE6">The YES price, in millionths.</param>
/// <param name="CountE2">Contracts, in hundredths.</param>
/// <param name="FeeE6">The fee, in millionths.</param>
/// <param name="OrderId">The order it filled.</param>
internal sealed record FillView(string Key, DateTimeOffset? Time, string Ticker, string Action, string Side, long YesPriceE6, long CountE2, long FeeE6, string OrderId);

/// <summary>The limits in the risk file, for the meters on the page. They change only by editing that file.</summary>
/// <param name="MaxOrderNotionalE6">Most cash one order may take.</param>
/// <param name="MaxOpenExposureE6">Most money at risk across the account.</param>
/// <param name="MaxDailyLossE6">The realized loss in a day at which trading stops.</param>
/// <param name="MaxOpenOrders">Most open orders.</param>
/// <param name="MaxOrderCountE2">Most contracts in one order.</param>
/// <param name="ProductionEnabled">Whether orders may be sent on the production account.</param>
internal sealed record LimitsView(long MaxOrderNotionalE6, long MaxOpenExposureE6, long MaxDailyLossE6, int MaxOpenOrders, long MaxOrderCountE2, bool ProductionEnabled)
{
    /// <summary>The limits of a risk policy.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The limits.</returns>
    public static LimitsView Of(RiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new LimitsView(policy.MaxOrderNotionalE6, policy.MaxOpenExposureE6, policy.MaxDailyLossE6, policy.MaxOpenOrders, policy.MaxOrderCountE2, policy.ProductionEnabled);
    }
}

/// <summary>Everything the Trading page shows, taken at one moment.</summary>
/// <param name="Production">Whether this is the real account.</param>
/// <param name="Synced">Whether the account has been read and nothing has been lost since (when not, new orders are refused).</param>
/// <param name="Fault">Why the live stream stopped, or empty.</param>
/// <param name="Halted">Whether the kill switch or the daily-loss limit halted new orders.</param>
/// <param name="HaltReason">Why.</param>
/// <param name="Shards">The balance on each shard.</param>
/// <param name="ExposureE6">Money at risk across the account.</param>
/// <param name="RealizedTodayE6">Today's realized profit (negative: loss).</param>
/// <param name="TransferredTodayE6">Collateral moved between shards today.</param>
/// <param name="Orders">The resting orders.</param>
/// <param name="Positions">The open positions.</param>
/// <param name="Fills">The latest fills, newest first.</param>
/// <param name="Limits">The risk limits.</param>
internal sealed record AccountView(
    bool Production,
    bool Synced,
    string Fault,
    bool Halted,
    string HaltReason,
    IReadOnlyList<ShardView> Shards,
    long ExposureE6,
    long RealizedTodayE6,
    long TransferredTodayE6,
    IReadOnlyList<OrderView> Orders,
    IReadOnlyList<PositionView> Positions,
    IReadOnlyList<FillView> Fills,
    LimitsView Limits);

/// <summary>An order the person wants to send, in the units the exchange uses: dollars at 10^-6, counts at 10^-2.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Buy"><see langword="true"/> to buy, <see langword="false"/> to sell.</param>
/// <param name="Yes"><see langword="true"/> for YES, <see langword="false"/> for NO.</param>
/// <param name="CountE2">Contracts, in hundredths.</param>
/// <param name="PriceE6">The limit price of the side traded, in millionths.</param>
/// <param name="ImmediateOrCancel">Immediate-or-cancel instead of good-till-cancelled.</param>
/// <param name="PostOnly">Never take liquidity.</param>
internal sealed record OrderEntry(string Ticker, bool Buy, bool Yes, long CountE2, long PriceE6, bool ImmediateOrCancel, bool PostOnly);

/// <summary>What would happen if an order were sent: the cost, the funding, the verdict. Nothing has been sent.</summary>
/// <param name="Lines">What to show the person, as the command line shows it.</param>
/// <param name="Ok">Whether the risk layer approved it.</param>
/// <param name="IsAmend">Whether it changes a resting order.</param>
/// <param name="Handle">What the trading link needs to send it; opaque here.</param>
internal sealed record OrderPlan(IReadOnlyList<string> Lines, bool Ok, bool IsAmend, object Handle);

/// <summary>What the kill switch did.</summary>
/// <param name="Cancelled">How many orders the follow-up passes cancelled.</param>
/// <param name="Remaining">Orders still resting at the end.</param>
/// <param name="Errors">What went wrong along the way.</param>
internal sealed record KillResult(int Cancelled, IReadOnlyList<string> Remaining, IReadOnlyList<string> Errors)
{
    /// <summary>Whether nothing is left resting and nothing went wrong.</summary>
    public bool Clean => Remaining.Count == 0 && Errors.Count == 0;
}
