using KalTerm.Data;
using KalTerm.Data.Metrics;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Tests;

public class ScreenerReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private ScreenerReader Reader() => new(new DataSource(db.Options));

    private static MarketMetrics Metrics(long volume, long? change = null, long? last = 500_000) =>
        new(Now, Now.AddMinutes(-1), last, change, volume, 3, 77, 490_000, 510_000, 20_000, 501_000, null, null, null, false);

    private async Task<int> SeedAsync(string ticker, long volume, long? change = null, long? last = 500_000, string status = "active")
    {
        var id = await ReaderSeed.MarketAsync(db, ticker, status);
        await new MarketMetricsStore(new DataSource(db.Options)).UpsertAsync([(id, Metrics(volume, change, last))], Now, TestContext.Current.CancellationToken);
        return id;
    }

    private static ScreenerQuery Query(ScreenerSort sort = ScreenerSort.Volume, bool descending = true, string? status = null, long? minVolume = null, ScreenerPosition? after = null, int limit = 100) =>
        new(sort, descending, status, minVolume, after, limit);

    private async Task<List<string>> TickersAsync(ScreenerQuery query)
    {
        var page = await Reader().ReadAsync(query, TestContext.Current.CancellationToken);
        return [.. page.Rows.Where(r => r.Ticker.StartsWith("SC-", StringComparison.Ordinal)).Select(r => r.Ticker)];
    }

    [Fact]
    public async Task MarketsAreRankedByVolumeLargestFirstOrSmallestFirstWithTheIdBreakingTies()
    {
        db.Require();
        await SeedAsync("SC-V-LOW", 100);
        await SeedAsync("SC-V-TIE-A", 500);
        await SeedAsync("SC-V-TIE-B", 500);
        await SeedAsync("SC-V-HIGH", 900);

        var largest = await TickersAsync(Query());
        var smallest = await TickersAsync(Query(descending: false));

        Assert.Equal(["SC-V-HIGH", "SC-V-TIE-A", "SC-V-TIE-B", "SC-V-LOW"], largest.Where(t => t.StartsWith("SC-V-", StringComparison.Ordinal)));
        Assert.Equal(["SC-V-LOW", "SC-V-TIE-A", "SC-V-TIE-B", "SC-V-HIGH"], smallest.Where(t => t.StartsWith("SC-V-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AMarketWithoutAValueForTheSortIsLastWhicheverWayTheListRuns()
    {
        db.Require();
        await SeedAsync("SC-N-BIG", 10, change: 50_000);
        await SeedAsync("SC-N-NONE", 20, change: null);
        await SeedAsync("SC-N-NEG", 30, change: -40_000);

        var down = (await TickersAsync(Query(ScreenerSort.Change))).Where(t => t.StartsWith("SC-N-", StringComparison.Ordinal));
        var up = (await TickersAsync(Query(ScreenerSort.Change, descending: false))).Where(t => t.StartsWith("SC-N-", StringComparison.Ordinal));

        Assert.Equal(["SC-N-BIG", "SC-N-NEG", "SC-N-NONE"], down);
        Assert.Equal(["SC-N-NEG", "SC-N-BIG", "SC-N-NONE"], up);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PagingByKeysetVisitsEveryMarketOnceEvenWithTiesAndMissingValues(bool descending)
    {
        db.Require();
        var name = descending ? "SC-P-D-" : "SC-P-A-";
        var expected = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            long? change = i % 3 == 0 ? null : (i % 2) * 1_000;   // missing, 0 and 1000, many ties
            await SeedAsync(name + i, 100 + i, change);
            expected.Add(name + i);
        }

        var seen = new List<string>();
        ScreenerPosition? after = null;
        var pages = 0;
        do
        {
            var page = await Reader().ReadAsync(Query(ScreenerSort.Change, descending, after: after, limit: 2), TestContext.Current.CancellationToken);
            seen.AddRange(page.Rows.Select(r => r.Ticker).Where(t => t.StartsWith(name, StringComparison.Ordinal)));
            after = page.Next;
            pages++;
        }
        while (after is not null && pages < 500);

        Assert.Equal(expected.Order(), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task StatusAndMinimumVolumeFilterTheList()
    {
        db.Require();
        await SeedAsync("SC-F-ACTIVE", 1_000);
        await SeedAsync("SC-F-SMALL", 50);
        await SeedAsync("SC-F-CLOSED", 1_000, status: "closed");

        var active = await TickersAsync(Query(status: "active"));
        var closed = await TickersAsync(Query(status: "closed"));
        var big = await TickersAsync(Query(minVolume: 1_000));

        Assert.Equal(["SC-F-ACTIVE", "SC-F-SMALL"], active.Where(t => t.StartsWith("SC-F-", StringComparison.Ordinal)).Order());
        Assert.Equal(["SC-F-CLOSED"], closed.Where(t => t.StartsWith("SC-F-", StringComparison.Ordinal)));
        Assert.Equal(["SC-F-ACTIVE", "SC-F-CLOSED"], big.Where(t => t.StartsWith("SC-F-", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public async Task ARowCarriesTheCatalogIdentityAndEveryStoredFigure()
    {
        db.Require();
        await SeedAsync("SC-R-ONE", 123_456, change: 20_000);

        var page = await Reader().ReadAsync(Query(minVolume: 123_456, limit: 1000), TestContext.Current.CancellationToken);
        var row = page.Rows.Single(r => r.Ticker == "SC-R-ONE");

        Assert.Equal(("EV-DEFAULT", "active", "Yes"), (row.EventTicker, row.Status, row.YesSubTitle));
        Assert.Equal((500_000L, 20_000L, 123_456L, 3L, 77L), (row.Metrics.LastE6, row.Metrics.Change24hE6, row.Metrics.VolumeE2, row.Metrics.TradeCount, row.Metrics.OpenInterestE2));
        Assert.Equal((490_000L, 510_000L, 20_000L, 501_000L, false), (row.Metrics.YesBidE6, row.Metrics.YesAskE6, row.Metrics.SpreadE6, row.Metrics.Vwap24hE6, row.Metrics.Partial));
        Assert.Equal((Now, DateTimeKind.Utc), (row.ChangedAt, row.ChangedAt.Kind));
    }

    [Fact]
    public async Task TheRefreshTimeIsTheStartOfTheNewestSuccessfulRunAndNoneUntilOneFinishes()
    {
        db.Require();
        Assert.Null((await Reader().ReadAsync(Query(limit: 1), TestContext.Current.CancellationToken)).RefreshedAt);
        await db.ExecuteAsync($"INSERT dbo.analyzer_run (name, started_at, finished_at, status) VALUES ('market_metrics', '{ReaderSeed.Time(Now.AddMinutes(-10))}', '{ReaderSeed.Time(Now.AddMinutes(-9))}', 'ok'), ('market_metrics', '{ReaderSeed.Time(Now.AddMinutes(-5))}', '{ReaderSeed.Time(Now.AddMinutes(-4))}', 'failed'), ('other', '{ReaderSeed.Time(Now)}', '{ReaderSeed.Time(Now)}', 'ok')");

        var page = await Reader().ReadAsync(Query(limit: 1), TestContext.Current.CancellationToken);

        Assert.Equal((Now.AddMinutes(-10), DateTimeKind.Utc), (page.RefreshedAt, page.RefreshedAt!.Value.Kind));
    }

    [Fact]
    public async Task TheLimitMustBeBetweenOneAndAThousand()
    {
        db.Require();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(Query(limit: 0), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(Query(limit: 1001), TestContext.Current.CancellationToken));
    }
}

public class StatusAnalyticsReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheStatusSnapshotCarriesTheNewestRunAndTheNewestSuccessfulOne()
    {
        db.Require();
        var reader = new StatusReader(new DataSource(db.Options));
        Assert.Null((await reader.ReadAsync(Now, TimeSpan.FromHours(24), TestContext.Current.CancellationToken)).Analytics!.Last);

        await db.ExecuteAsync($"INSERT dbo.analyzer_run (name, started_at, finished_at, status, row_count, message) VALUES ('market_metrics', '{ReaderSeed.Time(Now.AddMinutes(-10))}', '{ReaderSeed.Time(Now.AddMinutes(-9))}', 'ok', 5, '5 written, 0 removed'), ('market_metrics', '{ReaderSeed.Time(Now.AddMinutes(-5))}', '{ReaderSeed.Time(Now.AddMinutes(-4))}', 'failed', NULL, 'boom'), ('other', '{ReaderSeed.Time(Now)}', NULL, 'running', NULL, NULL)");

        var analytics = (await reader.ReadAsync(Now, TimeSpan.FromHours(24), TestContext.Current.CancellationToken)).Analytics!;

        Assert.Equal(("failed", "boom"), (analytics.Last!.Status, analytics.Last.Message));
        Assert.Equal(("ok", 5, Now.AddMinutes(-10), Now.AddMinutes(-9)), (analytics.LastOk!.Status, analytics.LastOk.Rows, analytics.LastOk.StartedAt, analytics.LastOk.FinishedAt));
        Assert.Equal(DateTimeKind.Utc, analytics.Last.StartedAt.Kind);
    }
}
