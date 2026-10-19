using System.Diagnostics;
using KalTerm.Data;
using KalTerm.Data.Markets;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.IngestTestKit;

namespace KalTerm.Ingest.Tests;

/// <summary>
/// Opt-in measurement (set KALTERM_THROUGHPUT=1): the database half of a discovery pass at the size the live check found
/// (about 122,000 open markets). A first pass inserts them all, a second updates them all, a third drops 100 and adds 100.
/// The numbers go to discovery-scale-summary.txt next to the test assembly. Not a pass/fail performance gate.
/// </summary>
public class DiscoveryScaleTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private const int Markets = 122_000;

    [Fact]
    public async Task InsertThenUpdateThenChurnAtLiveScale()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KALTERM_THROUGHPUT") == "1", "set KALTERM_THROUGHPUT=1 to run the scale measurement");
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        var ids = new MarketIdMap();
        var clock = new AutoAdvanceTimeProvider();
        var report = new List<string> { $"server {db.Options.Server}, {Markets:N0} markets, batches of 1,000" };

        var all = Enumerable.Range(0, Markets).Select(i => Open("SCALE-" + i.ToString("D6", CultureInfo.InvariantCulture))).ToList();
        report.Add(await PassAsync("insert pass", store, ids, clock, all));
        clock.Advance(TimeSpan.FromMinutes(5));
        report.Add(await PassAsync("update pass (nothing changed)", store, ids, clock, all));
        clock.Advance(TimeSpan.FromMinutes(5));
        var churn = all.Skip(100).Concat(Enumerable.Range(Markets, 100).Select(i => Open("SCALE-" + i.ToString("D6", CultureInfo.InvariantCulture)))).ToList();
        report.Add(await PassAsync("churn pass (100 gone, 100 new)", store, ids, clock, churn));

        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "discovery-scale-summary.txt"), report);
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }
    }

    private static async Task<string> PassAsync(string name, MarketStore store, MarketIdMap ids, AutoAdvanceTimeProvider clock, List<KalTerm.Kalshi.Models.Market> markets)
    {
        var timer = Stopwatch.StartNew();
        var result = await new MarketDiscovery(store, ids, Listing(markets), clock, NullLogger.Instance).RunAsync(TestContext.Current.CancellationToken);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {timer.Elapsed.TotalSeconds:F1} s; seen {result.Seen:N0}, new {result.Added.Count:N0}, updated {result.Matched:N0}, unlisted {result.Removed.Count:N0}");
    }
}
