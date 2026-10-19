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
        Assert.Equal(100, secondResult.Removed.Count(ticker => ticker.StartsWith("D-", StringComparison.Ordinal)));
        Assert.Equal(2_510, ids.Count);   // only this test's markets went through this map
        Assert.Equal(100L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE ticker LIKE 'D-%' AND status = 'unlisted'"));
        Assert.Equal(2_410L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE ticker LIKE 'D-%' AND status = 'active'"));
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

    [Fact]
    public async Task AnIncrementalPassRecordsASettlementAndAddsANewMarketInTheRealDatabase()
    {
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        var ids = new MarketIdMap();
        var clock = new AutoAdvanceTimeProvider();
        var updated = new UpdatedListing([Open("I-1").With("finalized"), Open("I-3"), Open("I-9").With("finalized")]);
        var discovery = new MarketDiscovery(store, ids, Listing([Open("I-1"), Open("I-2")]), clock, NullLogger.Instance, 1000, updated.Call);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));

        var result = await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(["I-3"], result.Added);
        Assert.Equal(["I-1"], result.Removed);
        Assert.Equal(1, result.Ignored);
        Assert.Equal("finalized", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'I-1'"));
        Assert.Equal("active", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'I-2'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE ticker = 'I-9'"));
    }
}
