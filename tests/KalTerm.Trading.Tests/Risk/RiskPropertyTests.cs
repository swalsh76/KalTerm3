using KalTerm.Trading.Risk;
using static KalTerm.Trading.Tests.Risk.RiskTestKit;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>
/// The property that matters: no order that exceeds a limit is ever approved. Random orders, accounts and policies, with every limit re-derived here without calling the code that enforces it, so a bug in the
/// checker cannot hide behind the same bug in the test.
/// </summary>
public class RiskPropertyTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(2026)]
    [InlineData(10_20)]
    public void AnApprovedOrderAlwaysSatisfiesEveryLimit(int seed)
    {
        var rng = new Random(seed);
        var approved = 0;
        var refused = 0;
        for (var run = 0; run < 15_000; run++)
        {
            var policy = new RiskPolicy
            {
                MaxOrderCountE2 = Pick(rng, 300, 500, 2_000),
                MaxPositionE2 = Pick(rng, 800, 2_000),
                MaxOrderNotionalE6 = Pick(rng, 1_000_000, 5_000_000, 20_000_000),
                MaxOpenExposureE6 = Pick(rng, 1_000_000, 3_000_000),
                MaxDailyLossE6 = Pick(rng, 1_000_000, 2_000_000),
                MaxOpenOrders = (int)Pick(rng, 2, 3, 5),
                MaxTransferPerOrderE6 = Pick(rng, 500_000, 2_000_000),
                MaxTransferPerDayE6 = Pick(rng, 1_000_000, 5_000_000),
                ProductionEnabled = rng.NextDouble() < 0.8,
            };
            var shards = Enumerable.Range(0, 4)
                .Select(i => new ShardState(i, rng.Next(0, 40_000) * 100L, rng.NextDouble() < 0.97, rng.NextDouble() < 0.95))
                .ToDictionary(s => s.Index);
            var markets = new Dictionary<string, MarketState>
            {
                ["T1"] = new(rng.Next(-400, 401), rng.Next(0, 201), rng.Next(0, 201)),
                ["T2"] = new(rng.Next(-400, 401), rng.Next(0, 201), rng.Next(0, 201)),
            };
            var snapshot = new AccountSnapshot(
                Now - TimeSpan.FromSeconds(rng.Next(-6, 12)),
                shards,
                markets,
                rng.Next(0, 4),
                rng.Next(0, 15_000) * 100L,
                rng.Next(-2_200_000, 500_001),
                rng.Next(0, 20_000) * 100L,
                rng.NextDouble() < 0.97,
                rng.NextDouble() < 0.02);
            var ticker = new[] { "T1", "T2", "T3" }[rng.Next(3)];
            var intent = new OrderIntent(
                ticker,
                rng.Next(0, 5),
                rng.Next(2) == 0 ? TradeAction.Buy : TradeAction.Sell,
                rng.Next(2) == 0 ? TradeOutcome.Yes : TradeOutcome.No,
                rng.Next(3) == 0 ? rng.Next(1, 2_501) : rng.Next(1, 301),
                rng.Next(1, 1_000_000),
                new long?[] { M1, M1, M1, M1, 2 * M1, 0, null }[rng.Next(7)]);
            var production = rng.NextDouble() < 0.2;

            var verdict = RiskChecker.Check(intent, snapshot, policy, Now, production);

            if (!verdict.Ok)
            {
                refused++;
                Assert.Empty(verdict.Funding);
                Assert.Null(verdict.Approved);
                Assert.NotEmpty(verdict.Refusals);
                continue;
            }

            approved++;
            AssertEveryLimitHolds(verdict.Approved!, intent, snapshot, policy, markets.GetValueOrDefault(ticker) ?? new MarketState(), shards[intent.ExchangeIndex], production);
        }

        Assert.True(approved > 300 && refused > 300, $"both branches must be exercised ({approved} approved, {refused} refused)");
    }

    private static long Pick(Random rng, params long[] values) => values[rng.Next(values.Length)];

    private static void AssertEveryLimitHolds(ApprovedOrder approved, OrderIntent intent, AccountSnapshot snapshot, RiskPolicy policy, MarketState market, ShardState shard, bool production)
    {
        var cost = approved.WorstCaseCostE6;
        Assert.True(!production || policy.ProductionEnabled);
        Assert.False(snapshot.Halted);
        Assert.True(snapshot.StreamOk && shard.TradingActive);
        Assert.True(intent.CountE2 <= policy.MaxOrderCountE2);
        Assert.True(cost <= policy.MaxOrderNotionalE6);
        Assert.True(snapshot.ExposureE6 + cost <= policy.MaxOpenExposureE6);
        Assert.True(snapshot.OpenOrders + 1 <= policy.MaxOpenOrders);
        Assert.True(snapshot.RealizedPnlTodayE6 > -policy.MaxDailyLossE6);
        var age = Now - snapshot.AsOf;
        Assert.True(age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(policy.SnapshotMaxAgeSeconds));
        Assert.True(intent.FeeMultiplierE6 is not null && approved.FeeE6 >= 0 && approved.FeeE6 % RiskChecker.FeeUnitE6 == 0);
        if (intent.AddsYes)
        {
            Assert.True(market.PositionE2 + market.RestingYesE2 + intent.CountE2 <= policy.MaxPositionE2);
        }
        else
        {
            Assert.True(-market.PositionE2 + market.RestingNoE2 + intent.CountE2 <= policy.MaxPositionE2);
        }

        var moved = approved.Funding.Sum(t => t.AmountE6);
        Assert.True(shard.BalanceE6 + moved >= cost);
        Assert.True(moved <= policy.MaxTransferPerOrderE6);
        Assert.True(snapshot.TransferredTodayE6 + moved <= policy.MaxTransferPerDayE6);
        var perSource = new Dictionary<int, long>();
        foreach (var transfer in approved.Funding)
        {
            Assert.Equal(intent.ExchangeIndex, transfer.Destination);
            Assert.NotEqual(transfer.Source, transfer.Destination);
            Assert.True(snapshot.Shards[transfer.Source].TransfersActive && transfer.AmountE6 > 0 && transfer.AmountE6 % 100 == 0);
            perSource[transfer.Source] = perSource.GetValueOrDefault(transfer.Source) + transfer.AmountE6;
        }

        Assert.All(perSource, pair => Assert.True(pair.Value <= snapshot.Shards[pair.Key].BalanceE6));
        if (moved > 0)
        {
            Assert.True(shard.TransfersActive);
        }
    }
}
