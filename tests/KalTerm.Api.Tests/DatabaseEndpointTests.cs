using System.Net;
using KalTerm.Contracts;
using KalTerm.Data;
using KalTerm.Data.Books;
using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.State;

namespace KalTerm.Api.Tests;

/// <summary>The whole stack with nothing faked: a real database, the real readers, the real pipeline. Skipped where no SQL Server is available (run on kaldb).</summary>
public sealed class DatabaseEndpointTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Day = new(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);

    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private async Task<int> MarketAsync(string ticker, string status = "active", string eventTicker = "EV-API")
    {
        var result = await new MarketStore(new DataSource(db.Options)).UpsertAsync([new MarketRecord(ticker, eventTicker, "binary", status, "Yes", "No")], DateTime.UtcNow, TestContext.Current.CancellationToken);
        return result.Ids[ticker];
    }

    private async Task DaysAsync(DateTime from, int days)
    {
        await using var connection = await db.OpenAsync();
        await PartitionManager.EnsureBoundariesAsync(connection, DateOnly.FromDateTime(from), DateOnly.FromDateTime(from).AddDays(days), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MarketsPageThroughTheRealCatalogAndOneMarketCanBeFetched()
    {
        db.Require();
        foreach (var ticker in new[] { "DB-C", "DB-A", "DB-B" })
        {
            await MarketAsync(ticker, eventTicker: "EV-DBPAGE");
        }

        await using var api = await ApiTestHost.StartWithDatabaseAsync(new DataSource(db.Options));
        var first = await api.GetAsync<MarketPage>("/v1/markets?event=EV-DBPAGE&limit=2");
        var second = await api.GetAsync<MarketPage>("/v1/markets?event=EV-DBPAGE&limit=2&cursor=" + first.NextCursor);
        var one = await api.GetAsync<MarketDto>("/v1/markets/DB-B");
        var (missing, _) = await api.GetAsync("/v1/markets/DB-NOPE");

        Assert.Equal(["DB-A", "DB-B"], first.Markets.Select(m => m.Ticker));
        Assert.Equal(["DB-C"], second.Markets.Select(m => m.Ticker));
        Assert.Null(second.NextCursor);
        Assert.Equal(("EV-DBPAGE", "active", "Yes", "No"), (one.EventTicker, one.Status, one.YesLabel, one.NoLabel));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TicksAndTradesPageExactlyAndComeBackAsExactDecimalText()
    {
        db.Require();
        await DaysAsync(Day, 1);
        var id = await MarketAsync("DB-FLOW");
        await db.ExecuteAsync(
            $"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, yes_bid_size_e2, volume_e2) VALUES "
            + $"('{Time(Day)}', {id}, 560000, 550000, 570000, 1000, 123400), ('{Time(Day.AddSeconds(1))}', {id}, 561000, 551000, 571000, 1000, 123500), "
            + $"('{Time(Day.AddSeconds(1))}', {id}, 562000, 552000, 572000, 1000, 123600), ('{Time(Day.AddSeconds(2))}', {id}, 563000, 553000, 573000, 1000, 123700)");
        await db.ExecuteAsync(
            $"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES "
            + $"('{Time(Day)}', {id}, 'tr-1', 400000, 600000, 300, 1, 0), ('{Time(Day.AddSeconds(1))}', {id}, 'tr-2', 410000, 590000, 1850, 2, 1)");

        await using var api = await ApiTestHost.StartWithDatabaseAsync(new DataSource(db.Options));
        var seenTicks = new List<string?>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/v1/markets/DB-FLOW/ticks?start=2026-03-10T10:00:00Z&end=2026-03-10T11:00:00Z&limit=2" + (cursor is null ? string.Empty : "&cursor=" + cursor);
            var page = await api.GetAsync<TickPage>(url);
            seenTicks.AddRange(page.Ticks.Select(t => t.Price));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        var latest = await api.GetAsync<TickPage>("/v1/markets/DB-FLOW/ticks?end=2026-03-10T11:00:00Z&limit=2");
        var trades = await api.GetAsync<TradePage>("/v1/markets/DB-FLOW/trades?start=2026-03-10T10:00:00Z&end=2026-03-10T11:00:00Z");

        Assert.Equal(["0.560000", "0.561000", "0.562000", "0.563000"], seenTicks);
        Assert.Equal(2, pages);
        Assert.Equal(["0.562000", "0.563000"], latest.Ticks.Select(t => t.Price));
        Assert.Equal("1234.00", (await api.GetAsync<TickPage>("/v1/markets/DB-FLOW/ticks?start=2026-03-10T10:00:00Z&end=2026-03-10T11:00:00Z&limit=1")).Ticks[0].Volume);
        Assert.Equal(["tr-1", "tr-2"], trades.Trades.Select(t => t.TradeId));
        Assert.Equal(("0.410000", "0.590000", "18.50", "no", true), (trades.Trades[1].YesPrice, trades.Trades[1].NoPrice, trades.Trades[1].Count, trades.Trades[1].TakerSide, trades.Trades[1].IsBlockTrade));
        var (unknown, _) = await api.GetAsync("/v1/markets/DB-NOPE/ticks");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task GapsAndTheStatusReportComeFromTheRealTablesAndReadyMeansTheDatabaseAnswers()
    {
        db.Require();
        var now = DateTime.UtcNow;
        await DaysAsync(now.AddDays(-1), 3);
        var id = await MarketAsync("DB-STATUS");
        await db.ExecuteAsync($"INSERT dbo.ticker (ts, market_id, price_e6) VALUES ('{Time(now.AddSeconds(-4))}', {id}, 500000)");
        await db.ExecuteAsync($"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES ('{Time(now.AddSeconds(-6))}', {id}, 'st-1', 500000, 500000, 100, 1, 0)");
        await new IngestStateStore(new DataSource(db.Options)).SetAsync(IngestStateKeys.Run, IngestStateKeys.Running, now.AddSeconds(-3), TestContext.Current.CancellationToken);
        await new GapStore(new DataSource(db.Options)).AppendAsync([new GapRecord(GapKinds.IngestDown, now.AddHours(-3), now.AddHours(-2), "restart")], TestContext.Current.CancellationToken);

        await using var api = await ApiTestHost.StartWithDatabaseAsync(new DataSource(db.Options));
        var gaps = await api.GetAsync<GapPage>("/v1/gaps");
        var status = await api.GetAsync<StatusDto>("/v1/status");
        var ready = await api.GetAsync<HealthDto>("/readyz");

        Assert.Equal(["restart"], gaps.Gaps.Select(g => g.Detail));
        Assert.Equal("running", status.Ingest.State);
        Assert.NotNull(status.Streams.Ticker.LastTime);
        Assert.NotNull(status.Streams.Trade.LastTime);
        Assert.Equal(1, status.Gaps.ByKind[GapKinds.IngestDown]);
        Assert.Equal(DatabaseFixture.MaxSizeBytes, status.Storage.MaxBytes);
        Assert.DoesNotContain(status.Problems, p => p.Contains("ingest", StringComparison.OrdinalIgnoreCase) || p.Contains("ticker", StringComparison.Ordinal) || p.Contains("trade data", StringComparison.Ordinal));
        Assert.Equal("ready", ready.Status);
    }

    [Fact]
    public async Task PublishedBooksComeBackThroughTheEndpointsAndTheStatusCountsThem()
    {
        db.Require();
        var now = DateTime.UtcNow;
        var live = await MarketAsync("DB-BOOK-A");
        var stale = await MarketAsync("DB-BOOK-B");
        await new BookStateStore(new DataSource(db.Options)).SaveAsync(
            [
                new BookState(live, now.AddSeconds(-5), now.AddSeconds(-1), false, false, [new BookLevel(410_000, 150), new BookLevel(400_000, 500)], [new BookLevel(550_000, 200)]),
                new BookState(stale, now, now.AddSeconds(-1), true, false, [], []),
            ],
            TestContext.Current.CancellationToken);

        await using var api = await ApiTestHost.StartWithDatabaseAsync(new DataSource(db.Options));
        var all = await api.GetAsync<OrderBookPage>("/v1/orderbooks");
        var one = await api.GetAsync<OrderBookDto>("/v1/markets/DB-BOOK-A/orderbook?depth=1");
        var (missing, _) = await api.GetAsync("/v1/markets/DB-NOPE/orderbook");
        var status = await api.GetAsync<StatusDto>("/v1/status");

        Assert.Equal(["DB-BOOK-A", "DB-BOOK-B"], all.Books.Select(b => b.Ticker).Where(t => t.StartsWith("DB-BOOK", StringComparison.Ordinal)));
        Assert.Equal([new BookLevelDto("0.410000", "1.50")], one.YesBids);
        Assert.Equal([new BookLevelDto("0.550000", "2.00")], one.NoBids);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((2, 1), (status.Books.Watched, status.Books.Stale));
        Assert.Contains(status.Problems, p => p.Contains("order books are stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATickWrittenWhileAStreamIsOpenArrivesThroughTheRealWatcherAndTheRealReaders()
    {
        db.Require();
        var now = DateTime.UtcNow;
        await DaysAsync(now.Date.AddDays(-1), 3);
        var id = await MarketAsync("DB-STREAM");
        await new BookStateStore(new DataSource(db.Options)).SaveAsync([new BookState(id, now.AddSeconds(-5), now.AddSeconds(-1), false, false, [new(560_000, 1_850)], [new(430_000, 5)])], TestContext.Current.CancellationToken);
        await using var api = await ApiTestHost.StartWithDatabaseAsUserAsync(new DataSource(db.Options));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/stream?ticks=DB-STREAM&books=DB-STREAM", UriKind.Relative));
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var ready = await NextEventAsync(reader);
        var book = await NextEventAsync(reader);
        await db.ExecuteAsync($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, volume_e2) VALUES ('{Time(DateTime.UtcNow)}', {id}, 570000, 560000, 580000, 5000)");
        List<string> tick;
        do
        {
            tick = await NextEventAsync(reader);   // keep-alives may come first; the tick arrives about two to three seconds after it was written
        }
        while (!tick.Contains("event: tick"));

        Assert.Equal("event: ready", ready[1]);
        Assert.Equal("event: book", book[0]);
        var book1 = ApiJson.Deserialize<OrderBookDto>(book[1]["data: ".Length..])!;
        Assert.Equal(("DB-STREAM", "0.560000", "0.570000"), (book1.Ticker, book1.YesBids[0].Price, book1.NoBids.Count == 1 ? "0.570000" : "x"));
        var dto = ApiJson.Deserialize<MarketTickDto>(tick.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])!;
        Assert.Equal(("DB-STREAM", "0.570000", "0.560000", "0.580000", "50.00"), (dto.Ticker, dto.Tick.Price, dto.Tick.YesBid, dto.Tick.YesAsk, dto.Tick.Volume));
    }

    [Fact]
    public async Task AMarketAddedToAnOpenStreamDeliversItsBookAndItsTicksThroughTheRealReadersAndRemovingItStopsThem()
    {
        db.Require();
        var now = DateTime.UtcNow;
        await DaysAsync(now.Date.AddDays(-1), 3);
        await MarketAsync("DB-LIVE-A");
        var added = await MarketAsync("DB-LIVE-B");
        await new BookStateStore(new DataSource(db.Options)).SaveAsync([new BookState(added, now.AddSeconds(-5), now.AddSeconds(-1), false, false, [new(410_000, 150)], [new(550_000, 200)])], TestContext.Current.CancellationToken);
        await using var api = await ApiTestHost.StartWithDatabaseAsUserAsync(new DataSource(db.Options));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/stream?ticks=DB-LIVE-A", UriKind.Relative));
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var streamId = ApiJson.Deserialize<StreamReadyDto>((await NextEventAsync(reader))[^1]["data: ".Length..])!.StreamId;

        var putTicks = await api.Client.PutAsync(new Uri($"/v1/stream/{streamId}/ticks/DB-LIVE-B", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var putBooks = await api.Client.PutAsync(new Uri($"/v1/stream/{streamId}/books/DB-LIVE-B", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var subscribedTicks = await NextEventWithAsync(reader, "event: subscribed");
        var subscribedBooks = await NextEventWithAsync(reader, "event: subscribed");
        var book = await NextEventWithAsync(reader, "event: book");
        await db.ExecuteAsync($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, volume_e2) VALUES ('{Time(DateTime.UtcNow)}', {added}, 570000, 560000, 580000, 5000)");
        var tick = await NextEventWithAsync(reader, "event: tick");
        var delete = await api.Client.DeleteAsync(new Uri($"/v1/stream/{streamId}/ticks/DB-LIVE-B", UriKind.Relative), TestContext.Current.CancellationToken);
        var unsubscribed = await NextEventWithAsync(reader, "event: unsubscribed");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent), (putTicks.StatusCode, putBooks.StatusCode, delete.StatusCode));
        var first = ApiJson.Deserialize<StreamSubscriptionDto>(subscribedTicks[^1]["data: ".Length..])!;
        Assert.Equal(("ticks", "DB-LIVE-B", false), (first.Kind, first.Ticker, first.Unknown));
        Assert.NotNull(first.From);
        Assert.Equal("books", ApiJson.Deserialize<StreamSubscriptionDto>(subscribedBooks[^1]["data: ".Length..])!.Kind);
        Assert.Equal("DB-LIVE-B", ApiJson.Deserialize<OrderBookDto>(book[^1]["data: ".Length..])!.Ticker);
        var dto = ApiJson.Deserialize<MarketTickDto>(tick[^1]["data: ".Length..])!;
        Assert.Equal(("DB-LIVE-B", "0.570000"), (dto.Ticker, dto.Tick.Price));
        Assert.Equal("ticks", ApiJson.Deserialize<StreamSubscriptionDto>(unsubscribed[^1]["data: ".Length..])!.Kind);
    }

    /// <summary>The next event with the given line, skipping keep-alives and other events.</summary>
    private static async Task<List<string>> NextEventWithAsync(StreamReader reader, string line)
    {
        while (true)
        {
            var next = await NextEventAsync(reader);
            if (next.Contains(line))
            {
                return next;
            }
        }
    }

    private static async Task<List<string>> NextEventAsync(StreamReader reader)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            if (line.Length == 0)
            {
                return lines;
            }

            lines.Add(line);
        }

        return lines;
    }
}
