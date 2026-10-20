using KalTerm.Trading.Risk;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>Builders the risk tests share.</summary>
internal static class RiskTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public const long M1 = 1_000_000;

    public static Dictionary<int, ShardState> Shards(params long[] balances) => Shards(true, balances);

    public static Dictionary<int, ShardState> Shards(bool active, params long[] balances) =>
        balances.Select((b, i) => new ShardState(i, b, active, true)).ToDictionary(s => s.Index);

    public static AccountSnapshot Snapshot(
        DateTimeOffset? asOf = null,
        Dictionary<int, ShardState>? shards = null,
        Dictionary<string, MarketState>? markets = null,
        int openOrders = 0,
        long exposure = 0,
        long pnl = 0,
        long transferred = 0,
        bool streamOk = true,
        bool halted = false) =>
        new(asOf ?? Now, shards ?? Shards(5_000_000, 0, 0, 0), markets ?? [], openOrders, exposure, pnl, transferred, streamOk, halted);

    public static OrderIntent Order(
        string ticker = "KXA-1",
        int shard = 0,
        TradeAction action = TradeAction.Buy,
        TradeOutcome outcome = TradeOutcome.Yes,
        long count = 100,
        long price = 500_000,
        long? fee = M1) =>
        new(ticker, shard, action, outcome, count, price, fee);

    public static Verdict Verdict(OrderIntent? intent = null, AccountSnapshot? snapshot = null, RiskPolicy? policy = null, bool production = false) =>
        RiskChecker.Check(intent ?? Order(), snapshot ?? Snapshot(), policy ?? new RiskPolicy(), Now, production);

    public static HashSet<RiskCode> Codes(Verdict verdict) => [.. verdict.Refusals.Select(r => r.Code)];
}
