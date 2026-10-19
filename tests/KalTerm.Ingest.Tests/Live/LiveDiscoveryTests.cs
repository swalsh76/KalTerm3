using System.Diagnostics;
using KalTerm.Data.Markets;
using KalTerm.Kalshi.Rest;
using static KalTerm.Kalshi.Tests.Live.LiveSupport;

namespace KalTerm.Ingest.Tests.Live;

/// <summary>Whether the live discovery check may run: only when asked for explicitly.</summary>
public static class LiveDiscoveryGate
{
    public static bool Enabled => Environment.GetEnvironmentVariable("KALSHI_INTEGRATION") == "1";
}

/// <summary>
/// Opt-in sizing check against the real Kalshi API with a read-only key (GET only; the client cannot write). It lists every open
/// non-combo market once, runs the real discovery over them against an in-memory store (no database is touched), and reports how many
/// markets there are and how long the pass took. It prints no key material and no account data; the numbers are public market data.
/// Run it like the other live tests (see CLAUDE.md); the summary lands in <c>live-summary.txt</c> next to the test assembly.
/// </summary>
public class LiveDiscoveryTests
{
    [Fact(Skip = Why, SkipUnless = nameof(LiveDiscoveryGate.Enabled), SkipType = typeof(LiveDiscoveryGate))]
    public async Task CountsTheOpenNonComboMarkets()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var events = new HashSet<string>(StringComparer.Ordinal);
            int total = 0, combos = 0, maxTicker = 0, maxEvent = 0, maxSub = 0, noClose = 0;
            var timer = Stopwatch.StartNew();
            await foreach (var market in client.IterateOpenMarketsAsync(TestContext.Current.CancellationToken))
            {
                total++;
                combos += market.IsMultivariate ? 1 : 0;
                statuses[market.Status] = statuses.GetValueOrDefault(market.Status) + 1;
                events.Add(market.EventTicker);
                maxTicker = Math.Max(maxTicker, market.Ticker.Length);
                maxEvent = Math.Max(maxEvent, market.EventTicker.Length);
                maxSub = Math.Max(maxSub, Math.Max(market.YesSubTitle.Length, market.NoSubTitle.Length));
                noClose += market.CloseTime is null ? 1 : 0;
                _ = MarketMapper.ToRecord(market);   // the mapping must not throw on real data
            }

            var listing = timer.Elapsed;
            Say($"open markets listed: {total:N0} ({combos:N0} combos slipped through the filter) in {listing.TotalSeconds:F1} s, about {(total + 999) / 1000} page requests");
            Say("status values: " + string.Join(", ", statuses.Select(s => $"{s.Key}={s.Value:N0}")));
            Say($"distinct events: {events.Count:N0}; longest ticker {maxTicker}, event ticker {maxEvent}, sub-title {maxSub} characters; markets without a close time: {noClose:N0}");

            var store = new FakeMarketStore();
            var discovery = new MarketDiscovery(store, new MarketIdMap(), client.IterateOpenMarketsAsync);
            var result = await discovery.RunAsync(TestContext.Current.CancellationToken);
            Say($"discovery pass over the same listing: seen {result.Seen:N0}, added {result.Added.Count:N0}, skipped combos {result.SkippedCombos:N0}, took {result.Duration.TotalSeconds:F1} s");

            Assert.True(total > 0, "no open markets were listed");
            Assert.Equal(0, combos);
            Assert.True(maxTicker <= 128 && maxEvent <= 128, "a ticker is longer than its 128-character column");
        }
    }
}
