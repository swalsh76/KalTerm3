using System.Globalization;
using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.Reading;
using KalTerm.Data.State;

namespace KalTerm.Data.Tests;

internal static class ReaderSeed
{
    public static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    public static async Task<int> MarketAsync(DatabaseFixture db, string ticker, string status = "active", string eventTicker = "EV-DEFAULT", DateTime? settled = null, long? settlementValueE6 = null)
    {
        var record = new MarketRecord(ticker, eventTicker, "binary", status, "Yes", "No", SettlementTs: settled, SettlementValueE6: settlementValueE6);
        var result = await new MarketStore(new DataSource(db.Options)).UpsertAsync([record], DateTime.UtcNow, TestContext.Current.CancellationToken);
        return result.Ids[ticker];
    }

    public static async Task DaysAsync(DatabaseFixture db, DateTime from, int days)
    {
        await using var connection = await db.OpenAsync();
        await PartitionManager.EnsureBoundariesAsync(connection, DateOnly.FromDateTime(from), DateOnly.FromDateTime(from).AddDays(days), TestContext.Current.CancellationToken);
    }

    public static string Time(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static Task TicksAsync(DatabaseFixture db, int marketId, params (DateTime Ts, long Price)[] ticks) =>
        db.ExecuteAsync(
            "INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, volume_e2) VALUES "
            + string.Join(", ", ticks.Select(t => FormattableString.Invariant($"('{Time(t.Ts)}', {marketId}, {t.Price}, {t.Price - 10}, {t.Price / 100})"))));

    public static Task TradesAsync(DatabaseFixture db, int marketId, params (DateTime Ts, string Id, long Yes, byte Side, bool Block)[] trades) =>
        db.ExecuteAsync(
            "INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES "
            + string.Join(", ", trades.Select(t => FormattableString.Invariant($"('{Time(t.Ts)}', {marketId}, '{t.Id}', {t.Yes}, {1_000_000 - t.Yes}, 300, {t.Side}, {(t.Block ? 1 : 0)})"))));
}

public class MarketReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private MarketReader Reader(ReaderOptions? options = null) => new(new DataSource(db.Options), options);

    [Fact]
    public async Task ListsInTickerOrderAndPagesExactly()
    {
        db.Require();
        foreach (var ticker in new[] { "PG-C", "PG-A", "PG-E", "PG-B", "PG-D" })
        {
            await ReaderSeed.MarketAsync(db, ticker, eventTicker: "EV-PAGING");
        }

        var filter = new MarketFilter(EventTicker: "EV-PAGING");
        var first = await Reader().ListAsync(filter, null, 2, TestContext.Current.CancellationToken);
        var second = await Reader().ListAsync(filter, first.Next, 2, TestContext.Current.CancellationToken);
        var third = await Reader().ListAsync(filter, second.Next, 2, TestContext.Current.CancellationToken);

        Assert.Equal(["PG-A", "PG-B"], first.Rows.Select(r => r.Ticker));
        Assert.Equal(new MarketPosition("PG-B"), first.Next);
        Assert.Equal(["PG-C", "PG-D"], second.Rows.Select(r => r.Ticker));
        Assert.Equal(["PG-E"], third.Rows.Select(r => r.Ticker));
        Assert.Null(third.Next);
    }

    [Fact]
    public async Task APageThatEndsExactlyAtTheLastMarketHasNoNextPosition()
    {
        db.Require();
        await ReaderSeed.MarketAsync(db, "EXACT-A", eventTicker: "EV-EXACT");
        await ReaderSeed.MarketAsync(db, "EXACT-B", eventTicker: "EV-EXACT");

        var page = await Reader().ListAsync(new MarketFilter(EventTicker: "EV-EXACT"), null, 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Rows.Count);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task FiltersByStatusAndEvent()
    {
        db.Require();
        await ReaderSeed.MarketAsync(db, "FL-OPEN", "active", "EV-FILTER");
        await ReaderSeed.MarketAsync(db, "FL-CLOSED", "closed", "EV-FILTER");
        await ReaderSeed.MarketAsync(db, "FL-OTHER", "closed", "EV-FILTER-OTHER");

        var closedInEvent = await Reader().ListAsync(new MarketFilter(Status: "closed", EventTicker: "EV-FILTER"), null, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["FL-CLOSED"], closedInEvent.Rows.Select(r => r.Ticker));
    }

    [Fact]
    public async Task SearchMatchesTheTextLiterallyEvenWithWildcardCharacters()
    {
        db.Require();
        foreach (var ticker in new[] { "SRCH-50%OFF", "SRCH-50XOFF", "SRCH_a", "SRCHxa", "SRCH[b]", "SRCHb" })
        {
            await ReaderSeed.MarketAsync(db, ticker, eventTicker: "EV-SEARCH");
        }

        async Task<string[]> Find(string text) =>
            [.. (await Reader().ListAsync(new MarketFilter(EventTicker: "EV-SEARCH", Search: text), null, 10, TestContext.Current.CancellationToken)).Rows.Select(r => r.Ticker)];

        Assert.Equal(["SRCH-50%OFF"], await Find("50%"));
        Assert.Equal(["SRCH_a"], await Find("_a"));
        Assert.Equal(["SRCH[b]"], await Find("[b]"));
        Assert.Equal(["SRCH-50%OFF", "SRCH-50XOFF"], await Find("srch-50"));   // case-insensitive, as the database's collation is
    }

    [Fact]
    public async Task GetReturnsEveryFieldOrNothing()
    {
        db.Require();
        var settled = new DateTime(2026, 3, 11, 12, 30, 15, 250, DateTimeKind.Utc);
        await ReaderSeed.MarketAsync(db, "GET-1", "finalized", "EV-GET", settled, 1_000_000);

        var market = await Reader().GetAsync("GET-1", TestContext.Current.CancellationToken);

        Assert.NotNull(market);
        Assert.Equal(("GET-1", "EV-GET", "binary", "finalized", "Yes", "No"), (market.Ticker, market.EventTicker, market.MarketType, market.Status, market.YesSubTitle, market.NoSubTitle));
        Assert.Equal(settled, market.SettlementTs);
        Assert.Equal(1_000_000L, market.SettlementValueE6);
        Assert.Null(market.OpenTime);
        Assert.Equal(string.Empty, market.Result);
        Assert.True(market.FirstSeenAt <= market.LastSeenAt);
        Assert.Null(await Reader().GetAsync("GET-NOPE", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BadArgumentsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ListAsync(new MarketFilter(), null, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ListAsync(new MarketFilter(), null, 1_001, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Reader().ListAsync(null!, null, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader().GetAsync(" ", TestContext.Current.CancellationToken));
    }
}

public class ReaderSupportTests
{
    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData("50%", "%50\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("[x]", "%\\[x]%")]
    [InlineData("back\\slash", "%back\\\\slash%")]
    public void LikePatternsEscapeTheWildcards(string text, string expected)
    {
        Assert.Equal(expected, ReaderSupport.LikePattern(text));
    }

    [Fact]
    public void OptionsThatCannotWorkAreRefusedByTheReaders()
    {
        var source = new DataSource(new DatabaseConnectionOptions());

        Assert.Throws<ArgumentOutOfRangeException>(() => new ReaderOptions { CommandTimeoutSeconds = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReaderOptions { LatestLookback = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketReader(source, new ReaderOptions { CommandTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickReader(source, new ReaderOptions { CommandTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradeReader(source, new ReaderOptions { CommandTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GapReader(source, new ReaderOptions { CommandTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusReader(source, new ReaderOptions { CommandTimeoutSeconds = 0 }));
        new ReaderOptions().Validate();
    }

    [Fact]
    public async Task EveryReaderRefusesBadArgumentsBeforeTouchingTheDatabase()
    {
        var source = new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" });
        var now = DateTime.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new TickReader(source).ReadAsync("T", null, now, null, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new TickReader(source).ReadAsync(" ", null, now, null, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new TradeReader(source).ReadAsync("T", null, now, null, 1_001, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new TradeReader(source).ReadAsync("", null, now, null, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new GapReader(source).ReadAsync(now.AddDays(-1), now, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new StatusReader(source).ReadAsync(now, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }
}

public class TickReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T10 = ReaderSeed.Day.AddHours(10);

    private TickReader Reader(ReaderOptions? options = null) => new(new DataSource(db.Options), options);

    private async Task<int> SeedAsync(string ticker)
    {
        await ReaderSeed.DaysAsync(db, ReaderSeed.Day, 1);
        var id = await ReaderSeed.MarketAsync(db, ticker);
        // Six updates; three share one millisecond, as real ones do.
        await ReaderSeed.TicksAsync(
            db,
            id,
            (T10, 100_000),
            (T10.AddSeconds(1), 110_000),
            (T10.AddSeconds(1), 120_000),
            (T10.AddSeconds(1), 130_000),
            (T10.AddSeconds(2), 140_000),
            (T10.AddSeconds(3), 150_000));
        return id;
    }

    [Fact]
    public async Task PagesForwardExactlyEvenWhenUpdatesShareATime()
    {
        db.Require();
        await SeedAsync("TK-PAGE");
        var end = T10.AddMinutes(1);
        var seen = new List<long?>();
        TickPosition? after = null;
        var pages = 0;

        do
        {
            var page = (await Reader().ReadAsync("TK-PAGE", T10, end, after, 2, TestContext.Current.CancellationToken))!;
            seen.AddRange(page.Rows.Select(r => r.PriceE6));
            after = page.Next;
            pages++;
        }
        while (after is not null);

        Assert.Equal([100_000L, 110_000, 120_000, 130_000, 140_000, 150_000], seen.Select(p => p!.Value));
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task TheNextPositionCountsTheRowsAlreadyReturnedAtTheSameTime()
    {
        db.Require();
        await SeedAsync("TK-POS");

        var first = (await Reader().ReadAsync("TK-POS", T10, T10.AddMinutes(1), null, 2, TestContext.Current.CancellationToken))!;
        var second = (await Reader().ReadAsync("TK-POS", null, T10.AddMinutes(1), first.Next, 2, TestContext.Current.CancellationToken))!;

        Assert.Equal(new TickPosition(T10.AddSeconds(1), 1), first.Next);
        Assert.Equal(new TickPosition(T10.AddSeconds(1), 3), second.Next);
    }

    [Fact]
    public async Task TheEndOfTheWindowIsExclusiveAndTheStartInclusive()
    {
        db.Require();
        await SeedAsync("TK-WIN");

        var page = (await Reader().ReadAsync("TK-WIN", T10.AddSeconds(1), T10.AddSeconds(3), null, 100, TestContext.Current.CancellationToken))!;

        Assert.Equal([110_000L, 120_000, 130_000, 140_000], page.Rows.Select(r => r.PriceE6!.Value));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task WithoutAStartItReturnsTheLatestRowsOldestFirstWithinTheLookback()
    {
        db.Require();
        await SeedAsync("TK-LATEST");

        var latest = (await Reader().ReadAsync("TK-LATEST", null, T10.AddMinutes(1), null, 2, TestContext.Current.CancellationToken))!;
        var tooLate = (await Reader().ReadAsync("TK-LATEST", null, T10.AddDays(8), null, 10, TestContext.Current.CancellationToken))!;
        var inside = (await Reader().ReadAsync("TK-LATEST", null, T10.AddDays(6), null, 10, TestContext.Current.CancellationToken))!;

        Assert.Equal([140_000L, 150_000], latest.Rows.Select(r => r.PriceE6!.Value));
        Assert.Null(latest.Next);
        Assert.Empty(tooLate.Rows);
        Assert.Equal(6, inside.Rows.Count);
    }

    [Fact]
    public async Task EveryStoredFieldComesBack()
    {
        db.Require();
        await ReaderSeed.DaysAsync(db, ReaderSeed.Day, 1);
        var id = await ReaderSeed.MarketAsync(db, "TK-FIELDS");
        await db.ExecuteAsync($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, yes_bid_size_e2, yes_ask_size_e2, volume_e2, open_interest_e2, last_trade_size_e2) VALUES ('{ReaderSeed.Time(T10)}', {id}, 560000, 550000, 570000, 1000, 2050, 123400, 9900, 1)");
        await db.ExecuteAsync($"INSERT dbo.ticker (ts, market_id) VALUES ('{ReaderSeed.Time(T10.AddSeconds(1))}', {id})");

        var page = (await Reader().ReadAsync("TK-FIELDS", T10, T10.AddMinutes(1), null, 10, TestContext.Current.CancellationToken))!;

        Assert.Equal(new StoredTick(T10, 560000, 550000, 570000, 1000, 2050, 123400, 9900, 1), page.Rows[0]);
        Assert.Equal(new StoredTick(T10.AddSeconds(1), null, null, null, null, null, null, null, null), page.Rows[1]);
    }

    [Fact]
    public async Task AnUnknownMarketIsNullNotEmpty()
    {
        db.Require();

        Assert.Null(await Reader().ReadAsync("TK-NOPE", null, T10, null, 10, TestContext.Current.CancellationToken));
    }
}

public class TradeReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T10 = ReaderSeed.Day.AddHours(10);

    private TradeReader Reader() => new(new DataSource(db.Options));

    private async Task SeedAsync(string ticker)
    {
        await ReaderSeed.DaysAsync(db, ReaderSeed.Day, 1);
        var id = await ReaderSeed.MarketAsync(db, ticker);
        await ReaderSeed.TradesAsync(
            db,
            id,
            (T10, "t-b", 400_000, 1, false),
            (T10, "t-a", 410_000, 2, true),
            (T10.AddSeconds(1), "t-c", 420_000, 0, false),
            (T10.AddSeconds(2), "t-d", 430_000, 1, false));
    }

    [Fact]
    public async Task PagesByTimeThenIdWithoutRepeatsOrGaps()
    {
        db.Require();
        await SeedAsync("TR-PAGE");
        var seen = new List<string>();
        TradePosition? after = null;

        do
        {
            var page = (await Reader().ReadAsync("TR-PAGE", T10, T10.AddMinutes(1), after, 1, TestContext.Current.CancellationToken))!;
            seen.AddRange(page.Rows.Select(r => r.TradeId));
            after = page.Next;
        }
        while (after is not null);

        Assert.Equal(["t-a", "t-b", "t-c", "t-d"], seen);
    }

    [Fact]
    public async Task TheNextPositionIsTheLastTradeReturned()
    {
        db.Require();
        await SeedAsync("TR-POS");

        var page = (await Reader().ReadAsync("TR-POS", T10, T10.AddMinutes(1), null, 3, TestContext.Current.CancellationToken))!;

        Assert.Equal(new TradePosition(T10.AddSeconds(1), "t-c"), page.Next);
    }

    [Fact]
    public async Task WithoutAStartItReturnsTheLatestTradesOldestFirst()
    {
        db.Require();
        await SeedAsync("TR-LATEST");

        var latest = (await Reader().ReadAsync("TR-LATEST", null, T10.AddMinutes(1), null, 3, TestContext.Current.CancellationToken))!;

        Assert.Equal(["t-b", "t-c", "t-d"], latest.Rows.Select(r => r.TradeId));
        Assert.Null(latest.Next);
    }

    [Fact]
    public async Task EveryStoredFieldComesBackAndAnUnknownMarketIsNull()
    {
        db.Require();
        await SeedAsync("TR-FIELDS");

        var page = (await Reader().ReadAsync("TR-FIELDS", T10, T10.AddMinutes(1), null, 10, TestContext.Current.CancellationToken))!;

        Assert.Equal(new StoredTrade(T10, "t-a", 410_000, 590_000, 300, 2, true), page.Rows[0]);
        Assert.Equal(new StoredTrade(T10.AddSeconds(1), "t-c", 420_000, 580_000, 300, 0, false), page.Rows[2]);
        Assert.Null(await Reader().ReadAsync("TR-NOPE", null, T10, null, 10, TestContext.Current.CancellationToken));
    }
}

public class GapReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReturnsGapsOverlappingTheWindowNewestFirstIncludingOpenOnes()
    {
        db.Require();
        await new GapStore(new DataSource(db.Options)).AppendAsync(
            [
                new GapRecord(GapKinds.WsReconnected, T0.AddHours(-10), T0.AddHours(-9), "before the window"),
                new GapRecord(GapKinds.WriterDropped, T0.AddHours(-1), T0.AddMinutes(-50), "inside"),
                new GapRecord(GapKinds.IngestDown, T0.AddMinutes(-30), T0.AddMinutes(-20), "inside, newer"),
                new GapRecord(GapKinds.WsLost, T0.AddMinutes(-5), null, "still open"),
                new GapRecord(GapKinds.WsDown, T0.AddHours(2), T0.AddHours(3), "after the window"),
            ],
            TestContext.Current.CancellationToken);

        var gaps = await new GapReader(new DataSource(db.Options)).ReadAsync(T0.AddHours(-2), T0, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["still open", "inside, newer", "inside"], gaps.Select(g => g.Detail));
        Assert.Null(gaps[0].EndedAt);
        Assert.Equal(GapKinds.IngestDown, gaps[1].Kind);
        Assert.Equal(T0.AddMinutes(-30), gaps[1].StartedAt);
    }

    [Fact]
    public async Task TheLimitKeepsTheNewest()
    {
        db.Require();
        await new GapStore(new DataSource(db.Options)).AppendAsync(
            [new GapRecord(GapKinds.WsLost, T0.AddDays(-30), T0.AddDays(-30).AddMinutes(1), "limit-old"), new GapRecord(GapKinds.WsLost, T0.AddDays(-29), T0.AddDays(-29).AddMinutes(1), "limit-new")],
            TestContext.Current.CancellationToken);

        var gaps = await new GapReader(new DataSource(db.Options)).ReadAsync(T0.AddDays(-31), T0.AddDays(-28), 1, TestContext.Current.CancellationToken);

        Assert.Equal(["limit-new"], gaps.Select(g => g.Detail));
    }
}

public class StatusReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ReportsTheRunStateFreshnessStorageMarketsAndGaps()
    {
        db.Require();
        var now = DateTime.UtcNow;
        await new PartitionMaintenance(new DataSource(db.Options)).EnsureAheadAsync(DateOnly.FromDateTime(now).AddDays(-1), 3, TestContext.Current.CancellationToken);
        var id = await ReaderSeed.MarketAsync(db, "ST-OPEN", "active");
        await ReaderSeed.MarketAsync(db, "ST-CLOSED", "closed");
        await ReaderSeed.MarketAsync(db, "ST-CLOSED2", "closed");
        var lastTick = now.AddMinutes(-2);
        var lastTrade = now.AddMinutes(-20);   // older than the first freshness window, so a wider one finds it
        await ReaderSeed.TicksAsync(db, id, (now.AddMinutes(-3), 100_000), (lastTick, 110_000));
        await ReaderSeed.TradesAsync(db, id, (lastTrade, "st-1", 400_000, 1, false));
        await new IngestStateStore(new DataSource(db.Options)).SetAsync(IngestStateKeys.Run, IngestStateKeys.Running, now.AddSeconds(-5), TestContext.Current.CancellationToken);
        await new GapStore(new DataSource(db.Options)).AppendAsync(
            [new GapRecord(GapKinds.IngestDown, now.AddHours(-2), now.AddHours(-1)), new GapRecord(GapKinds.IngestDown, now.AddHours(-3), now.AddHours(-2)), new GapRecord(GapKinds.WsLost, now.AddDays(-3), now.AddDays(-3).AddMinutes(1))],
            TestContext.Current.CancellationToken);

        var status = await new StatusReader(new DataSource(db.Options)).ReadAsync(now, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(IngestStateKeys.Running, status.Run!.Value);
        Assert.InRange((now - status.Run.UpdatedAt).TotalSeconds, 4, 6);
        Assert.Equal(DateTimeKind.Utc, status.Run.UpdatedAt.Kind);
        Assert.True(Math.Abs((status.LastTickerAt!.Value - lastTick).TotalMilliseconds) < 5);
        Assert.True(Math.Abs((status.LastTradeAt!.Value - lastTrade).TotalMilliseconds) < 5);
        Assert.True(status.DataFileBytes > 0);
        Assert.Equal(DatabaseFixture.MaxSizeBytes, status.MaxFileBytes);
        Assert.True(status.DiskFreeBytes is null or > 0);
        Assert.Equal(1, status.MarketsByStatus["active"]);
        Assert.Equal(2, status.MarketsByStatus["closed"]);
        Assert.Equal(2, status.GapsByKind[GapKinds.IngestDown]);
        Assert.False(status.GapsByKind.ContainsKey(GapKinds.WsLost));   // older than the 24-hour window
    }
}

public class StatusReaderEmptyTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AFreshDatabaseHasNothingToReportButItsSize()
    {
        db.Require();

        var status = await new StatusReader(new DataSource(db.Options)).ReadAsync(DateTime.UtcNow, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Null(status.Run);
        Assert.Null(status.LastTickerAt);
        Assert.Null(status.LastTradeAt);
        Assert.Empty(status.MarketsByStatus);
        Assert.Empty(status.GapsByKind);
        Assert.True(status.DataFileBytes > 0);
    }
}
