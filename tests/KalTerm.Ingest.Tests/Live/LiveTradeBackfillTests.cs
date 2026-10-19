using KalTerm.Core.Money;
using KalTerm.Ingest.Backfill;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using static KalTerm.Kalshi.Tests.Live.LiveSupport;

namespace KalTerm.Ingest.Tests.Live;

/// <summary>
/// Opt-in check of the trade backfill's read side against the real Kalshi API with a read-only key (GET only; no database is touched). It reads a short window that ended
/// ten minutes ago through <see cref="RestTradeSource"/>, once whole and once as two halves cut the way the backfiller cuts slices, and checks that the halves add up to the
/// whole with no repeated trade id, that every trade falls inside its window, and that a trade's yes and no prices add up to one dollar. It prints no key material and no
/// account data; the numbers are public market data. Run it like the other live tests (see CLAUDE.md), after the owner opts in.
/// </summary>
public class LiveTradeBackfillTests
{
    private static async Task<List<Trade>> ReadAsync(RestTradeSource source, DateTimeOffset from, DateTimeOffset until)
    {
        var trades = new List<Trade>();
        await foreach (var trade in source.TradesAsync(from, until, TestContext.Current.CancellationToken))
        {
            trades.Add(trade);
        }

        return trades;
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveDiscoveryGate.Enabled), SkipType = typeof(LiveDiscoveryGate))]
    public async Task AWindowReadInTwoSlicesAddsUpToTheWholeWindowWithNoRepeats()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            var source = new RestTradeSource(client);
            var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());
            var start = end.AddMinutes(-3);
            var middle = start.AddSeconds(90);

            var whole = await ReadAsync(source, start, end);
            var first = await ReadAsync(source, start, middle.AddSeconds(-1));
            var second = await ReadAsync(source, middle, end);

            Say($"trades {start:HH:mm:ss}-{end:HH:mm:ss} UTC: {whole.Count} whole, {first.Count} + {second.Count} in two slices");
            Say($"  first: {whole.FirstOrDefault()?.Ticker} {whole.FirstOrDefault()?.YesPriceDollars} x {whole.FirstOrDefault()?.CountFp}");
            Assert.True(whole.Count > 100, $"expected more than 100 trades in three minutes, got {whole.Count}");
            Assert.Equal(whole.Count, first.Count + second.Count);
            Assert.Equal(whole.Count, whole.Select(t => (t.Ticker, t.TradeId)).Distinct().Count());
            Assert.Equal(whole.Select(t => t.TradeId).Order(), first.Concat(second).Select(t => t.TradeId).Order());
            Assert.All(whole, t => Assert.InRange(t.CreatedTime, start, end.AddSeconds(1)));
            Assert.All(whole, t => Assert.Equal(Dollars.One, t.YesPriceDollars + t.NoPriceDollars));
        }
    }
}
