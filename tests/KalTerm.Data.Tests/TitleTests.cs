using KalTerm.Data.Markets;
using KalTerm.Data.Metrics;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Tests;

/// <summary>The human-readable names: a market's own title, its event's title and sub-title, how they are stored, found and read.</summary>
public class TitleTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MarketStore Markets() => new(new DataSource(db.Options));

    private EventStore Events() => new(new DataSource(db.Options));

    private static MarketRecord Market(string ticker, string eventTicker, string title = "", string yes = "Yes") => new(ticker, eventTicker, "binary", "active", yes, "No", Title: title);

    /// <summary>Every event any other test's market mentions gets a stored event, so only the ones this test makes itself can be missing.</summary>
    private Task QuietTheRestAsync() => db.ExecuteAsync(
        "INSERT dbo.market_event (event_ticker, series_ticker, title, sub_title, known, fetched_at) SELECT DISTINCT event_ticker, '', N'other', N'', 1, SYSUTCDATETIME() FROM dbo.market "
        + "WHERE event_ticker NOT IN ('TT-E-NEW1', 'TT-E-NEW2', 'TT-E-KNOWN', 'TT-E-OLD', 'TT-E-RECENT') AND event_ticker NOT IN (SELECT event_ticker FROM dbo.market_event)");

    [Fact]
    public async Task AMarketsTitleIsStoredAndChangesWhenKalshiChangesIt()
    {
        db.Require();
        await Markets().UpsertAsync([Market("TT-M-1", "TT-E-1", "Philadelphia wins")], Now, Ct);
        Assert.Equal("Philadelphia wins", await db.ScalarAsync<string>("SELECT title FROM dbo.market WHERE ticker = 'TT-M-1'"));

        await Markets().UpsertAsync([Market("TT-M-1", "TT-E-1", "Philadelphia to win")], Now.AddMinutes(5), Ct);

        Assert.Equal("Philadelphia to win", await db.ScalarAsync<string>("SELECT title FROM dbo.market WHERE ticker = 'TT-M-1'"));
    }

    [Fact]
    public async Task ATitleLongerThanTheColumnIsCutNotRefused()
    {
        db.Require();

        await Markets().UpsertAsync([Market("TT-M-LONG", "TT-E-LONG", new string('x', 600))], Now, Ct);

        Assert.Equal(512, await db.ScalarAsync<int>("SELECT LEN(title) FROM dbo.market WHERE ticker = 'TT-M-LONG'"));
    }

    [Fact]
    public async Task EventsAreInsertedUpdatedAndTheLastOfARepeatedTickerWins()
    {
        db.Require();
        var store = Events();

        await store.UpsertAsync([new("TT-E-A", "SER", "Old title", "Old sub"), new("TT-E-B", "SER", "B", string.Empty), new("TT-E-B", "SER", "B again", "Sub B")], Now, Ct);
        await store.UpsertAsync([new("TT-E-A", "SER2", "New title", "New sub")], Now.AddHours(1), Ct);

        Assert.Equal(("SER2", "New title", "New sub", true), await Row("TT-E-A"));
        Assert.Equal(("SER", "B again", "Sub B", true), await Row("TT-E-B"));
        Assert.Equal(Now.AddHours(1), await db.ScalarAsync<DateTime>("SELECT fetched_at FROM dbo.market_event WHERE event_ticker = 'TT-E-A'"));
        await store.UpsertAsync([], Now, Ct);   // an empty batch does nothing
    }

    private async Task<(string Series, string Title, string Sub, bool Known)> Row(string ticker)
    {
        await using var connection = await db.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT series_ticker, title, sub_title, known FROM dbo.market_event WHERE event_ticker = @t", connection);
        command.Parameters.AddWithValue("@t", ticker);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }

    [Fact]
    public async Task AnEventKalshiDoesNotKnowNeverReplacesOneItDoesButIsReplacedByOne()
    {
        db.Require();
        var store = Events();
        await store.UpsertAsync([new("TT-E-K", "SER", "Known", "Sub")], Now, Ct);

        await store.UpsertAsync([new("TT-E-K", string.Empty, string.Empty, string.Empty, Known: false)], Now.AddDays(1), Ct);
        Assert.Equal(("SER", "Known", "Sub", true), await Row("TT-E-K"));
        Assert.Equal(Now.AddDays(1), await db.ScalarAsync<DateTime>("SELECT fetched_at FROM dbo.market_event WHERE event_ticker = 'TT-E-K'"));   // but the asking is remembered

        await store.UpsertAsync([new("TT-E-U", string.Empty, string.Empty, string.Empty, Known: false)], Now, Ct);
        Assert.False((await Row("TT-E-U")).Known);
        await store.UpsertAsync([new("TT-E-U", "SER", "Found", "Later")], Now.AddDays(2), Ct);
        Assert.Equal(("SER", "Found", "Later", true), await Row("TT-E-U"));
    }

    [Fact]
    public async Task EventsAMarketMentionsWithNoEventStoredAreMissingNeverAskedFirstAndAnOldUnknownIsAskedAgain()
    {
        db.Require();
        await QuietTheRestAsync();
        await Markets().UpsertAsync([Market("TT-M-N1", "TT-E-NEW1"), Market("TT-M-N2", "TT-E-NEW2"), Market("TT-M-K", "TT-E-KNOWN"), Market("TT-M-OLD", "TT-E-OLD"), Market("TT-M-RECENT", "TT-E-RECENT")], Now, Ct);
        await Events().UpsertAsync([new("TT-E-KNOWN", "SER", "Known", string.Empty)], Now, Ct);
        await Events().UpsertAsync([new("TT-E-OLD", string.Empty, string.Empty, string.Empty, Known: false)], Now.AddDays(-8), Ct);
        await Events().UpsertAsync([new("TT-E-RECENT", string.Empty, string.Empty, string.Empty, Known: false)], Now.AddDays(-1), Ct);

        var missing = await Events().MissingAsync(100, Now.AddDays(-7), Ct);

        Assert.Equal(["TT-E-NEW1", "TT-E-NEW2", "TT-E-OLD"], missing);   // never asked first; a known event and a recently asked unknown one are left alone
        Assert.Equal(["TT-E-NEW1", "TT-E-NEW2"], await Events().MissingAsync(2, Now.AddDays(-7), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Events().MissingAsync(0, Now, Ct));
    }

    [Fact]
    public async Task AMarketIsReadWithItsOwnTitleAndItsEventsTitleAndSubTitleAndAMarketWithoutAnEventHasEmptyOnes()
    {
        db.Require();
        await Markets().UpsertAsync([Market("TT-R-1", "TT-E-R1", "Philadelphia wins"), Market("TT-R-2", "TT-E-R2-NONE", "No event")], Now, Ct);
        await Events().UpsertAsync([new("TT-E-R1", "SER", "Philadelphia vs New York", "PHI vs NYK (Oct 20)")], Now, Ct);
        var reader = new MarketReader(new DataSource(db.Options));

        var one = await reader.GetAsync("TT-R-1", Ct);
        var two = await reader.GetAsync("TT-R-2", Ct);
        var list = await reader.ListAsync(new MarketFilter(EventTicker: "TT-E-R1"), null, 10, Ct);

        Assert.Equal(("Philadelphia wins", "Philadelphia vs New York", "PHI vs NYK (Oct 20)"), (one!.Title, one.EventTitle, one.EventSubTitle));
        Assert.Equal(("No event", string.Empty, string.Empty), (two!.Title, two.EventTitle, two.EventSubTitle));
        Assert.Equal("Philadelphia vs New York", Assert.Single(list.Rows).EventTitle);
    }

    private async Task SeedScreenerAsync(string ticker, string eventTicker, string title, string yes, long volume)
    {
        var result = await Markets().UpsertAsync([Market(ticker, eventTicker, title, yes)], Now, Ct);
        var metrics = new MarketMetrics(Now, Now.AddMinutes(-1), 500_000, null, volume, 3, 77, 490_000, 510_000, 20_000, 501_000, null, null, null, false);
        await new MarketMetricsStore(new DataSource(db.Options)).UpsertAsync([(result.Ids[ticker], metrics)], Now, Ct);
    }

    [Fact]
    public async Task TheScreenerCarriesTheTitlesAndASearchFindsAMarketByAnyOfThem()
    {
        db.Require();
        await SeedScreenerAsync("TT-S-1", "TT-E-S1", "Philadelphia wins", "Philadelphia", 900);
        await SeedScreenerAsync("TT-S-2", "TT-E-S1", "New York wins", "New York", 800);
        await SeedScreenerAsync("TT-S-3", "TT-E-S3-NONE", "Will it rain in Seattle?", "Yes", 700);
        await Events().UpsertAsync([new("TT-E-S1", "SER", "Philadelphia vs New York", "PHI vs NYK (Oct 20)")], Now, Ct);
        var reader = new ScreenerReader(new DataSource(db.Options));
        ScreenerQuery Ask(string text) => new(ScreenerSort.Volume, true, null, null, null, 100, null, text);
        async Task<List<string>> Found(string text) => [.. (await reader.ReadAsync(Ask(text), Ct)).Rows.Select(r => r.Ticker).Where(t => t.StartsWith("TT-S-", StringComparison.Ordinal))];

        var all = (await reader.ReadAsync(Ask("TT-S-"), Ct)).Rows.OrderBy(r => r.Ticker).ToList();

        Assert.Equal(("Philadelphia wins", "Philadelphia vs New York", "PHI vs NYK (Oct 20)"), (all[0].Title, all[0].EventTitle, all[0].EventSubTitle));
        Assert.Equal(("Will it rain in Seattle?", string.Empty, string.Empty), (all[2].Title, all[2].EventTitle, all[2].EventSubTitle));
        Assert.Equal(["TT-S-1", "TT-S-2"], await Found("philadelphia vs new"));   // the event title: both markets of the game
        Assert.Equal(["TT-S-1"], await Found("PHILADELPHIA WINS"));   // the market title, any case
        Assert.Equal(["TT-S-1", "TT-S-2"], await Found("phi vs nyk"));   // the event sub-title
        Assert.Equal(["TT-S-3"], await Found("rain in seattle"));   // a market whose event is not stored is still found by its own title
        Assert.Empty(await Found("no-such-words-anywhere"));
    }
}
