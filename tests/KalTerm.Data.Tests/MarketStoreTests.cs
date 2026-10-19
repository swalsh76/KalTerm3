using KalTerm.Data.Markets;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Tests;

public class MarketStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MarketRecord Record(string ticker, string status = "active") => new(ticker, "EV-" + ticker, "binary", status, "Yes", "No");

    private MarketStore Store() => new(new DataSource(db.Options));

    [Fact]
    public async Task InsertsNewMarketsAndReturnsTheirIds()
    {
        db.Require();

        var result = await Store().UpsertAsync([Record("INS-A"), Record("INS-B")], T0, TestContext.Current.CancellationToken);

        Assert.Equal(["INS-A", "INS-B"], result.Inserted.Order());
        Assert.Equal(0, result.Matched);
        Assert.Equal(2, result.Ids.Count);
        Assert.NotEqual(result.Ids["INS-A"], result.Ids["INS-B"]);
        Assert.Equal(result.Ids["INS-A"], await db.ScalarAsync<int>("SELECT market_id FROM dbo.market WHERE ticker = 'INS-A'"));
    }

    [Fact]
    public async Task UpdatingKeepsTheIdAndFirstSeenAndMovesLastSeen()
    {
        db.Require();
        var store = Store();
        var first = await store.UpsertAsync([Record("UPD-A") with { CloseTime = T0 }], T0, TestContext.Current.CancellationToken);

        var later = T0.AddMinutes(5);
        var second = await store.UpsertAsync([Record("UPD-A", "closed") with { YesSubTitle = "Changed", CloseTime = T0.AddHours(1) }], later, TestContext.Current.CancellationToken);

        Assert.Empty(second.Inserted);
        Assert.Equal(1, second.Matched);
        Assert.Equal(first.Ids["UPD-A"], second.Ids["UPD-A"]);
        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT status, yes_sub_title, close_time, first_seen_at, last_seen_at FROM dbo.market WHERE ticker = 'UPD-A'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("closed", reader.GetString(0));
        Assert.Equal("Changed", reader.GetString(1));
        Assert.Equal(T0.AddHours(1), reader.GetDateTime(2));
        Assert.Equal(T0, reader.GetDateTime(3));
        Assert.Equal(later, reader.GetDateTime(4));
    }

    [Fact]
    public async Task HandlesMoreThanOnePageOfMarkets()
    {
        db.Require();
        var records = Enumerable.Range(0, 2_500).Select(i => Record("BIG-" + i.ToString("D5", CultureInfo.InvariantCulture))).ToList();

        var result = await Store().UpsertAsync(records, T0, TestContext.Current.CancellationToken);

        Assert.Equal(2_500, result.Inserted.Count);
        Assert.Equal(2_500, result.Ids.Values.Distinct().Count());
        Assert.Equal(2_500L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.market WHERE ticker LIKE 'BIG-%'"));
    }

    [Fact]
    public async Task ATickerRepeatedInOneBatchCountsOnceAndTheLastRecordWins()
    {
        db.Require();

        var result = await Store().UpsertAsync([Record("DUP-A", "unopened"), Record("DUP-A", "active")], T0, TestContext.Current.CancellationToken);

        Assert.Equal(["DUP-A"], result.Inserted);
        Assert.Equal("active", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'DUP-A'"));
    }

    [Fact]
    public async Task EveryFieldRoundTripsIncludingNullsAndExtremes()
    {
        db.Require();
        var full = new MarketRecord(
            "FLD-A", "EV-FLD", "scalar", "settled", new string('y', 300), "No side", T0, T0.AddHours(1), T0.AddDays(1), T0.AddDays(2), "yes", long.MaxValue);

        await Store().UpsertAsync([full, Record("FLD-B")], T0, TestContext.Current.CancellationToken);

        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT event_ticker, market_type, status, yes_sub_title, no_sub_title, open_time, close_time, latest_expiration_time, settlement_ts, result, settlement_value_e6 FROM dbo.market WHERE ticker IN ('FLD-A', 'FLD-B') ORDER BY ticker", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("EV-FLD", "scalar", "settled"), (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        Assert.Equal(256, reader.GetString(3).Length);
        Assert.Equal("No side", reader.GetString(4));
        Assert.Equal([T0, T0.AddHours(1), T0.AddDays(1), T0.AddDays(2)], new[] { reader.GetDateTime(5), reader.GetDateTime(6), reader.GetDateTime(7), reader.GetDateTime(8) });
        Assert.Equal("yes", reader.GetString(9));
        Assert.Equal(long.MaxValue, reader.GetInt64(10));
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        for (var column = 5; column <= 8; column++)
        {
            Assert.True(reader.IsDBNull(column));
        }

        Assert.True(reader.IsDBNull(10));
        Assert.Equal(string.Empty, reader.GetString(9));
    }

    [Fact]
    public async Task AnEmptyBatchDoesNothing()
    {
        var result = await Store().UpsertAsync([], T0, TestContext.Current.CancellationToken);

        Assert.Same(MarketUpsertResult.Empty, result);
    }

    [Fact]
    public async Task LoadIdsReturnsEveryKnownTicker()
    {
        db.Require();
        var store = Store();
        var inserted = await store.UpsertAsync([Record("LOAD-A"), Record("LOAD-B")], T0, TestContext.Current.CancellationToken);

        var ids = await store.LoadIdsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(inserted.Ids["LOAD-A"], ids["LOAD-A"]);
        Assert.Equal(inserted.Ids["LOAD-B"], ids["LOAD-B"]);
    }

    [Fact]
    public async Task ANullBatchIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store().UpsertAsync(null!, T0, TestContext.Current.CancellationToken));
    }
}
