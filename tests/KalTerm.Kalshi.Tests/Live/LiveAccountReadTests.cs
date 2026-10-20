using KalTerm.Core.Money;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Kalshi.Tests.Live;

/// <summary>
/// M6 slice 6.3: the typed account reads against the real account (read-only key, GET only). It checks properties that must hold for any account and prints counts and yes or no, never a balance, a price,
/// a count of contracts or an id.
/// </summary>
public sealed class LiveAccountReadTests
{
    private const string Why = LiveSupport.Why;

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheTypedAccountReadsParseWhatTheRealAccountHolds()
    {
        var (settings, signer) = LiveSupport.Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            var token = TestContext.Current.CancellationToken;

            var balance = await client.BalanceAsync(cancellationToken: token);
            var shards = balance.BalanceBreakdown ?? [];
            var sum = shards.Aggregate(Dollars.Zero, (total, shard) => total + shard.Balance);
            LiveSupport.Say($"balance: {shards.Count} shards; the shards add up to the total: {sum == balance.BalanceDollars}; the cents field is the dollars cut to whole cents: {balance.Balance == balance.BalanceDollars.Micros / 10_000}");
            Assert.Equal(balance.BalanceDollars, sum);
            Assert.Equal(balance.BalanceDollars.Micros / 10_000, balance.Balance);

            var narrowed = await client.BalanceAsync(exchangeIndex: shards.Count > 0 ? shards[0].ExchangeIndex : 0, cancellationToken: token);
            LiveSupport.Say($"one shard asked for alone: gives the same dollars as its row in the breakdown: {shards.Count == 0 || narrowed.BalanceDollars == shards[0].Balance}");

            var positions = await client.PositionsPageAsync(KalshiQuery.Empty.With("limit", 5).With("settlement_status", "all"), token);
            LiveSupport.Say($"positions: {positions.MarketPositions?.Count ?? 0} market, {positions.EventPositions?.Count ?? 0} event (first page, settled ones included)");

            var orders = await client.OrdersPageAsync(KalshiQuery.Empty.With("limit", 20), token);
            var listed = orders.Orders ?? [];
            LiveSupport.Say($"orders: {listed.Count} on the first page; statuses seen: {string.Join(", ", listed.Select(o => o.Status).Distinct().Order())}");
            Assert.All(listed, o =>
            {
                Assert.Equal(Dollars.One, o.YesPriceDollars + o.NoPriceDollars);
                Assert.Equal(o.InitialCountFp, o.FillCountFp + o.RemainingCountFp);
                Assert.Equal("yes", o.Side);
            });
            LiveSupport.Say("every order: yes price plus no price is a dollar; ordered = filled + remaining; sits on the yes book");

            var fills = await client.FillsPageAsync(KalshiQuery.Empty.With("limit", 20), token);
            var filled = fills.Fills ?? [];
            LiveSupport.Say($"fills: {filled.Count} on the first page");
            Assert.All(filled, f =>
            {
                Assert.Equal(Dollars.One, f.YesPriceDollars + f.NoPriceDollars);
                Assert.True(f.CountFp > Contracts.Zero);
                Assert.True(f.FeeCost >= Dollars.Zero);
            });
            LiveSupport.Say("every fill: prices add to a dollar; a positive count; a fee of zero or more");

            if (listed.Count > 0)
            {
                var again = await client.OrderAsync(listed[0].OrderId, token);
                LiveSupport.Say($"one order read by its id equals the same order in the list: {again == listed[0]}");
                Assert.Equal(listed[0], again);
            }

            var settlements = await client.SettlementsPageAsync(KalshiQuery.Empty.With("limit", 20), token);
            LiveSupport.Say($"settlements: {settlements.Settlements?.Count ?? 0} on the first page");

            var resting = await ToListAsync(client.IterateOrdersAsync(KalshiQuery.Empty.With("status", "resting"), token));
            var queue = resting.Count == 0 ? [] : await client.QueuePositionsAsync(KalshiQuery.Empty.With("market_tickers", string.Join(',', resting.Select(o => o.Ticker).Distinct().Take(5))), token);
            LiveSupport.Say($"resting orders: {resting.Count}; queue positions answered for {queue.Count}");
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
