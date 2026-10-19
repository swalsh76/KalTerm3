using KalTerm.Data;
using KalTerm.Data.Markets;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.IngestTestKit;

namespace KalTerm.Ingest.Tests;

/// <summary>Discovery against the real store and a real database: the two halves together.</summary>
public class DiscoveryDatabaseTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task TwoPassesOver2500MarketsReportExactlyWhatChanged()
    {
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        var ids = new MarketIdMap();
        var clock = new AutoAdvanceTimeProvider();
        var first = Enumerable.Range(0, 2_500).Select(i => Open("D-" + i.ToString("D5", CultureInfo.InvariantCulture))).ToList();
        var firstResult = await new MarketDiscovery(store, ids, Listing(first), clock, NullLogger.Instance).RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        var second = first.Skip(100).Concat(Enumerable.Range(2_500, 10).Select(i => Open("D-" + i.ToString("D5", CultureInfo.InvariantCulture)))).ToList();
        var secondResult = await new MarketDiscovery(store, ids, Listing(second), clock, NullLogger.Instance).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2_500, firstResult.Added.Count);
        Assert.Equal(2_400, secondResult.Matched);
        Assert.Equal(10, secondResult.Added.Count);
        Assert.Equal(100, secondResult.Removed.Count);
        Assert.Equal(2_510, ids.Count);
        Assert.Equal(100L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE status = 'unlisted'"));
        Assert.Equal(2_410L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE status = 'active'"));
    }

    [Fact]
    public async Task AFreshProcessRebuildsTheSameIdsFromTheDatabase()
    {
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        var ids = new MarketIdMap();
        await new MarketDiscovery(store, ids, Listing([Open("R-1"), Open("R-2")]), new AutoAdvanceTimeProvider()).RunAsync(TestContext.Current.CancellationToken);

        var restarted = new MarketIdMap();
        await restarted.LoadAsync(store, TestContext.Current.CancellationToken);

        Assert.True(ids.TryGetId("R-2", out var before));
        Assert.True(restarted.TryGetId("R-2", out var after));
        Assert.Equal(before, after);
    }
}
