using System.Diagnostics;
using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;
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
    /// <summary>
    /// Checks the incremental listing the 30-second windows rely on: that <c>min_updated_ts</c> with <c>mve_filter=exclude</c> is accepted,
    /// that adding <c>status</c> is refused or not, and how many markets a 2-minute, 5-minute and 60-minute look-back returns. Bounded to a few pages.
    /// </summary>
    [Fact(Skip = Why, SkipUnless = nameof(LiveDiscoveryGate.Enabled), SkipType = typeof(LiveDiscoveryGate))]
    public async Task TheUpdatedSinceListingIsAcceptedAndSmall()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            const int maxPages = 5;
            var now = DateTimeOffset.UtcNow;
            foreach (var minutes in new[] { 2, 5, 60 })
            {
                var since = now.AddMinutes(-minutes);
                var timer = Stopwatch.StartNew();
                var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var count = 0;
                var combos = 0;
                string? cursor = null;
                var pages = 0;
                do
                {
                    var page = await client.MarketsPageAsync(
                        KalshiQuery.Empty.With("min_updated_ts", since).With("mve_filter", "exclude").With("limit", 1000).With("cursor", cursor),
                        TestContext.Current.CancellationToken);
                    pages++;
                    foreach (var market in page.Markets)
                    {
                        count++;
                        combos += market.IsMultivariate ? 1 : 0;
                        statuses[market.Status] = statuses.GetValueOrDefault(market.Status) + 1;
                    }

                    cursor = page.Cursor.Length == 0 ? null : page.Cursor;
                }
                while (cursor is not null && pages < maxPages);

                Say($"min_updated_ts = now - {minutes} min (+ mve_filter=exclude): {count:N0} markets in {pages} page(s), {timer.Elapsed.TotalSeconds:F1} s, more pages: {cursor is not null}, combos: {combos}; statuses: {string.Join(", ", statuses.Select(s => $"{s.Key}={s.Value:N0}"))}");
                Assert.Equal(0, combos);
            }

            try
            {
                var withStatus = await client.MarketsPageAsync(
                    KalshiQuery.Empty.With("min_updated_ts", now.AddMinutes(-5)).With("status", "open").With("mve_filter", "exclude").With("limit", 5),
                    TestContext.Current.CancellationToken);
                Say($"min_updated_ts + status=open: accepted, {withStatus.Markets.Count} markets on the first page of 5");
            }
            catch (KalshiApiException ex)
            {
                Say($"min_updated_ts + status=open: refused with HTTP {ex.StatusCode}");
            }
        }
    }

    /// <summary>One full pass, then two incremental passes 30 seconds apart, as the schedule would run them; in-memory store, no database.</summary>
    [Fact(Skip = Why, SkipUnless = nameof(LiveDiscoveryGate.Enabled), SkipType = typeof(LiveDiscoveryGate))]
    public async Task AFullPassThenIncrementalPassesBehaveAsScheduled()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            var discovery = new MarketDiscovery(
                new FakeMarketStore(),
                new MarketIdMap(),
                client.IterateOpenMarketsAsync,
                listUpdatedSince: client.IterateMarketsUpdatedSinceAsync);

            var full = await discovery.RunAsync(PassKind.Full, TestContext.Current.CancellationToken);
            Say($"full pass: {full.Seen:N0} stored, {full.Duration.TotalSeconds:F1} s");
            for (var i = 1; i <= 2; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                var incremental = await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);
                Say($"incremental pass {i}: {incremental.Kind}, {incremental.Seen:N0} stored ({incremental.Matched:N0} known, {incremental.Added.Count:N0} new), {incremental.Removed.Count:N0} no longer tradable, {incremental.Ignored:N0} ignored (history or not yet open), {incremental.SkippedCombos} combos, {incremental.Duration.TotalSeconds:F1} s");
                Assert.Equal(PassKind.Incremental, incremental.Kind);
                Assert.True(incremental.Duration < TimeSpan.FromSeconds(30), "an incremental pass must be shorter than the 30-second tick");
            }
        }
    }

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
