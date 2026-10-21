using System.Globalization;
using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Trading.Risk;

/// <summary>
/// The risk layer: every order passes through <see cref="Check"/> before anything can be sent. It is a pure function of (order, account snapshot, policy, clock): no I/O, no globals, so it can be tested
/// exhaustively, and it is the only way to obtain an <see cref="ApprovedOrder"/>. Cancels and the kill switch are never checked: nothing is ever refused to them.
/// </summary>
/// <remarks>
/// Rules that shape everything here. <b>Fail closed</b>: a missing, stale or future-dated snapshot, a private stream that is down, an unknown or inactive shard, and an unknown fee multiplier all refuse the
/// order. <b>Worst case</b>: the cost is the most the order can take from the balance, fee included; the fee peaks at a fill price of 0.50 (it is <c>P x (1 - P)</c>) and a limit order can fill at any price at
/// or better than its limit, so the fee is taken at the worst price the limit allows and rounded up to a cent. <b>Exact numbers</b>: dollars are millionths, counts hundredths, nothing is a float.
/// If the order's shard lacks the balance, the verdict carries the smallest transfers from other shards that would cover it, largest free balance first; whether Kalshi already deducts resting-order collateral
/// from a shard's balance is checked on the real account in slice 6.7.
/// </remarks>
public static class RiskChecker
{
    /// <summary>Transfers are whole centicents ($0.0001), in millionths.</summary>
    public const long CenticentE6 = 100;

    /// <summary>Fees are rounded up to a cent per order, the conservative alignment, in millionths.</summary>
    public const long FeeUnitE6 = 10_000;

    private const long OneDollarE6 = 1_000_000;
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decides whether <paramref name="intent"/> may be sent. Every rule runs, in the fixed order below, so the person sees all the reasons at once instead of fixing them one at a time; only a missing
    /// snapshot stops early (nothing is known about the account, so only the production switch and the fee are checked). Order: production switch, fee, snapshot, shard, per-order size, per-market position,
    /// account capacity, daily loss, funding.
    /// </summary>
    /// <param name="intent">What the person wants.</param>
    /// <param name="snapshot">The picture of the account, or <see langword="null"/> when there is none yet.</param>
    /// <param name="policy">The limits.</param>
    /// <param name="now">The current time.</param>
    /// <param name="production">Whether the order is for the production account.</param>
    /// <returns>The verdict.</returns>
    public static Verdict Check(OrderIntent intent, AccountSnapshot? snapshot, RiskPolicy policy, DateTimeOffset now, bool production)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(policy);
        var refusals = new List<Refusal>();
        if (production && !policy.ProductionEnabled)
        {
            refusals.Add(new Refusal(RiskCode.ProductionDisabled, "trading on the production account is switched off (production_enabled = false in risk.json)"));
        }

        var fee = WorstCaseFeeE6(intent);
        if (fee is null)
        {
            refusals.Add(new Refusal(RiskCode.UnknownFee, $"{intent.Ticker}: the fee cannot be modelled, so the cost is unknown"));
        }

        if (snapshot is null)
        {
            refusals.Add(new Refusal(RiskCode.NoSnapshot, "no account snapshot yet, so limits cannot be checked"));
            return new Verdict(null, refusals, fee, null, []);
        }

        refusals.AddRange(SnapshotRefusals(snapshot, policy, now));
        var (shard, shardRefusals) = ShardRefusals(snapshot, intent);
        refusals.AddRange(shardRefusals);

        var market = snapshot.Markets.TryGetValue(intent.Ticker, out var known) ? known : new MarketState();
        var cost = fee is null ? (long?)null : Saturate((Int128)CollateralE6(intent, market.PositionE2) + fee.Value);

        refusals.AddRange(SizeRefusals(intent, cost, policy));
        refusals.AddRange(PositionRefusals(intent, market, policy));
        refusals.AddRange(CapacityRefusals(snapshot, cost, policy));
        var loss = DailyLossRefusal(snapshot, policy);
        if (loss is not null)
        {
            refusals.Add(loss);
        }

        var (funding, fundingRefusals) = Funding(snapshot, shard, cost, policy);
        refusals.AddRange(fundingRefusals);

        if (refusals.Count > 0)
        {
            return new Verdict(null, refusals, fee, cost, [], TripKill: loss is not null);
        }

        var approved = new ApprovedOrder(intent, fee!.Value, cost!.Value, funding, production, now);
        return new Verdict(approved, [], fee, cost, funding);
    }

    /// <summary>The largest fee the order can incur, or <see langword="null"/> when the fee cannot be modelled.</summary>
    /// <param name="intent">The order.</param>
    /// <returns>The fee in millionths, rounded up to a cent.</returns>
    public static long? WorstCaseFeeE6(OrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.FeeMultiplierE6 is not { } multiplier || multiplier < 0)
        {
            return null;
        }

        const long half = OneDollarE6 / 2;
        // A buy fills at or below its limit, a sell at or above it; the fee peaks at 0.50.
        var feePrice = intent.Action == TradeAction.Buy ? Math.Min(intent.PriceE6, half) : Math.Max(intent.PriceE6, half);
        try
        {
            var fee = KalshiFees.TakerFee(FeeMultiplier.FromMicros(multiplier), Contracts.FromHundredths(intent.CountE2), Dollars.FromMicros(feePrice));
            return KalshiFees.RoundUp(fee, Dollars.FromMicros(FeeUnitE6)).Micros;
        }
        catch (OverflowException)
        {
            return null;   // an amount so large the fee does not fit: the cost is unknown, which refuses the order
        }
    }

    /// <summary>
    /// Cash the order can lock up, fee excluded. A buy locks <c>count x limit</c>. A sell first closes what is held of that outcome (no new collateral); anything beyond is selling short, that is buying the other
    /// side at <c>1 - limit</c>.
    /// </summary>
    /// <param name="intent">The order.</param>
    /// <param name="positionE2">The net YES position in the market, in hundredths.</param>
    /// <returns>The collateral in millionths.</returns>
    public static long CollateralE6(OrderIntent intent, long positionE2)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Action == TradeAction.Buy)
        {
            return Saturate(CeilDiv((Int128)intent.CountE2 * intent.PriceE6, 100));
        }

        var held = Math.Max(intent.Outcome == TradeOutcome.Yes ? positionE2 : -positionE2, 0);
        var opening = Math.Max(intent.CountE2 - held, 0);
        return Saturate(CeilDiv((Int128)opening * (OneDollarE6 - intent.PriceE6), 100));
    }

    /// <summary>Transfers that bring <paramref name="target"/> up by <paramref name="needE6"/>: from the richest shards first, only whole centicents, never more than a source holds.</summary>
    /// <param name="snapshot">The account.</param>
    /// <param name="target">The shard that needs money.</param>
    /// <param name="needE6">How much it needs.</param>
    /// <returns>The transfers (empty when none are needed), or <see langword="null"/> when the account cannot cover it.</returns>
    public static IReadOnlyList<Transfer>? PlanFunding(AccountSnapshot snapshot, int target, long needE6)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (needE6 <= 0)
        {
            return [];
        }

        var need = RoundUp(needE6, CenticentE6);
        if (!snapshot.Shards[target].TransfersActive)
        {
            return null;
        }

        var sources = snapshot.Shards.Values
            .Where(s => s.Index != target && s.TransfersActive)
            .OrderByDescending(s => s.BalanceE6)
            .ThenBy(s => s.Index);
        var plan = new List<Transfer>();
        foreach (var source in sources)
        {
            if (need <= 0)
            {
                break;
            }

            var take = Math.Min(need, source.BalanceE6 - (source.BalanceE6 % CenticentE6));
            if (take > 0)
            {
                plan.Add(new Transfer(source.Index, target, take));
                need -= take;
            }
        }

        return need <= 0 ? plan : null;
    }

    /// <summary>Dollars to four places, or six when the value is finer than a centicent. This is the risk layer's own text for refusals and the audit log, from whole numbers; it is not the display format of <c>ValueText</c>.</summary>
    /// <param name="valueE6">The amount in millionths of a dollar.</param>
    /// <returns>For example <c>$0.2200</c>.</returns>
    public static string DollarsText(long valueE6)
    {
        var sign = valueE6 < 0 ? "-" : string.Empty;
        var magnitude = valueE6 == long.MinValue ? (Int128)long.MaxValue + 1 : Math.Abs(valueE6);
        var whole = (long)(magnitude / OneDollarE6);
        var fraction = (long)(magnitude % OneDollarE6);
        var digits = fraction % CenticentE6 != 0
            ? fraction.ToString("D6", CultureInfo.InvariantCulture)
            : (fraction / CenticentE6).ToString("D4", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}${whole}.{digits}");
    }

    /// <summary>Contracts without trailing zeros: <c>5</c>, <c>0.5</c>, <c>12.34</c>.</summary>
    /// <param name="valueE2">The count in hundredths.</param>
    /// <returns>The text.</returns>
    public static string CountText(long valueE2)
    {
        var sign = valueE2 < 0 ? "-" : string.Empty;
        var magnitude = Int128.Abs(valueE2);
        var whole = (long)(magnitude / 100);
        var fraction = (long)(magnitude % 100);
        return fraction == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{whole}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{whole}.{fraction:D2}").TrimEnd('0');
    }

    private static List<Refusal> SnapshotRefusals(AccountSnapshot snapshot, RiskPolicy policy, DateTimeOffset now)
    {
        var refusals = new List<Refusal>();
        if (snapshot.Halted)
        {
            refusals.Add(new Refusal(RiskCode.Halted, "trading is halted by the kill switch; no new orders until it is cleared"));
        }

        var age = now - snapshot.AsOf;
        if (age > TimeSpan.FromSeconds(policy.SnapshotMaxAgeSeconds) || age < -FutureTolerance)
        {
            refusals.Add(new Refusal(
                RiskCode.StaleSnapshot,
                string.Create(CultureInfo.InvariantCulture, $"the account snapshot is {Math.Round(age.TotalSeconds, MidpointRounding.ToEven):0}s old (limit {policy.SnapshotMaxAgeSeconds}s)")));
        }

        if (!snapshot.StreamOk)
        {
            refusals.Add(new Refusal(RiskCode.StreamDown, "the private account stream is down, so positions may be stale"));
        }

        return refusals;
    }

    private static (ShardState? Shard, List<Refusal> Refusals) ShardRefusals(AccountSnapshot snapshot, OrderIntent intent)
    {
        if (!snapshot.Shards.TryGetValue(intent.ExchangeIndex, out var shard))
        {
            return (null, [new Refusal(RiskCode.UnknownShard, string.Create(CultureInfo.InvariantCulture, $"exchange shard {intent.ExchangeIndex} is not in the snapshot"))]);
        }

        return shard.TradingActive
            ? (shard, [])
            : (shard, [new Refusal(RiskCode.ShardNotTrading, string.Create(CultureInfo.InvariantCulture, $"trading is not active on exchange shard {shard.Index}"))]);
    }

    private static List<Refusal> SizeRefusals(OrderIntent intent, long? cost, RiskPolicy policy)
    {
        var refusals = new List<Refusal>();
        if (intent.CountE2 > policy.MaxOrderCountE2)
        {
            refusals.Add(new Refusal(RiskCode.OrderTooLarge, $"{CountText(intent.CountE2)} contracts exceeds the limit of {CountText(policy.MaxOrderCountE2)} per order"));
        }

        if (cost is { } worst && worst > policy.MaxOrderNotionalE6)
        {
            refusals.Add(new Refusal(RiskCode.NotionalTooLarge, $"worst-case cost {DollarsText(worst)} exceeds the per-order limit of {DollarsText(policy.MaxOrderNotionalE6)}"));
        }

        return refusals;
    }

    private static List<Refusal> PositionRefusals(OrderIntent intent, MarketState market, RiskPolicy policy)
    {
        var (worst, side) = intent.AddsYes
            ? ((Int128)market.PositionE2 + market.RestingYesE2 + intent.CountE2, "YES")
            : ((Int128)(-market.PositionE2) + market.RestingNoE2 + intent.CountE2, "NO");
        return worst > policy.MaxPositionE2
            ? [new Refusal(RiskCode.PositionLimit, $"{intent.Ticker}: this could leave {CountText(Saturate(worst))} {side} contracts (limit {CountText(policy.MaxPositionE2)} per market)")]
            : [];
    }

    private static List<Refusal> CapacityRefusals(AccountSnapshot snapshot, long? cost, RiskPolicy policy)
    {
        var refusals = new List<Refusal>();
        if (snapshot.OpenOrders + 1 > policy.MaxOpenOrders)
        {
            refusals.Add(new Refusal(RiskCode.TooManyOrders, string.Create(CultureInfo.InvariantCulture, $"{snapshot.OpenOrders} orders are already open (limit {policy.MaxOpenOrders})")));
        }

        if (cost is { } worst)
        {
            var reached = Saturate((Int128)snapshot.ExposureE6 + worst);
            if (reached > policy.MaxOpenExposureE6)
            {
                refusals.Add(new Refusal(RiskCode.ExposureLimit, $"exposure would reach {DollarsText(reached)} (limit {DollarsText(policy.MaxOpenExposureE6)})"));
            }
        }

        return refusals;
    }

    private static Refusal? DailyLossRefusal(AccountSnapshot snapshot, RiskPolicy policy) =>
        snapshot.RealizedPnlTodayE6 <= -policy.MaxDailyLossE6
            ? new Refusal(RiskCode.DailyLoss, $"today's realized loss {DollarsText(-snapshot.RealizedPnlTodayE6)} has reached the daily limit of {DollarsText(policy.MaxDailyLossE6)}")
            : null;

    private static (IReadOnlyList<Transfer> Funding, List<Refusal> Refusals) Funding(AccountSnapshot snapshot, ShardState? shard, long? cost, RiskPolicy policy)
    {
        if (cost is not { } needed || shard is null || !shard.TradingActive)
        {
            return ([], []);   // nothing to fund; the reason is already a refusal
        }

        var planned = PlanFunding(snapshot, shard.Index, needed - shard.BalanceE6);
        if (planned is null)
        {
            var free = snapshot.Shards.Values.Sum(s => s.BalanceE6);
            var why = needed > free
                ? $"the account has only {DollarsText(free)} in total, {DollarsText(needed - free)} short"
                : "the other shards' funds cannot be moved right now";
            return ([], [new Refusal(
                RiskCode.InsufficientFunds,
                string.Create(CultureInfo.InvariantCulture, $"the order needs {DollarsText(needed)} on shard {shard.Index}, which has {DollarsText(shard.BalanceE6)}; {why}"))]);
        }

        var moved = planned.Sum(t => t.AmountE6);
        if (moved > policy.MaxTransferPerOrderE6)
        {
            return (planned, [new Refusal(RiskCode.TransferLimit, $"funding the shard needs a {DollarsText(moved)} transfer (limit {DollarsText(policy.MaxTransferPerOrderE6)} per order)")]);
        }

        if (snapshot.TransferredTodayE6 + moved > policy.MaxTransferPerDayE6)
        {
            return (planned, [new Refusal(
                RiskCode.TransferLimit,
                $"funding the shard would move {DollarsText(snapshot.TransferredTodayE6 + moved)} today (limit {DollarsText(policy.MaxTransferPerDayE6)})")]);
        }

        return (planned, []);
    }

    private static Int128 CeilDiv(Int128 numerator, long denominator) => (numerator + denominator - 1) / denominator;

    private static long RoundUp(long value, long unit) => ((value + unit - 1) / unit) * unit;

    private static long Saturate(Int128 value) => value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;
}
