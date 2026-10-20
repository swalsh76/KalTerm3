using System.Net;
using KalTerm.Contracts;
using KalTerm.Data.Candles;
using KalTerm.Data.Reading;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

/// <summary>The real pipeline and endpoints over fake readers: what each endpoint reads, what it sends, and what it refuses.</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private ApiTestHost _api = null!;

    private FakeReaders Fakes => _api.Fakes;

    public async ValueTask InitializeAsync() => _api = await ApiTestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task AssertInvalidAsync(string url, params string[] fields)
    {
        var (response, body) = await _api.GetAsync(url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = ApiJson.Deserialize<ErrorDto>(body)!;
        Assert.Equal("invalid_parameter", error.Error);
        Assert.Equal(fields, error.Fields);
    }

    // ---- health ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HealthIsAlwaysOkAndReadinessFollowsTheDatabase()
    {
        Assert.Equal("ok", (await _api.GetAsync<HealthDto>("/healthz")).Status);
        Assert.Equal("ready", (await _api.GetAsync<HealthDto>("/readyz")).Status);

        Fakes.Readiness.Ready = false;
        var (response, body) = await _api.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("not_ready", ApiJson.Deserialize<HealthDto>(body)!.Status);
    }

    // ---- markets --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListingMarketsUsesTheDefaultsAndMapsTheRows()
    {
        Fakes.Markets.Page = new Page<StoredMarket, MarketPosition>([Market("A-1"), Market("A-2", "closed")], null);

        var page = await _api.GetAsync<MarketPage>("/v1/markets");

        var (filter, after, limit) = Assert.Single(Fakes.Markets.Lists);
        Assert.Equal((new MarketFilter(), null, 100), (filter, after, limit));
        Assert.Equal(["A-1", "A-2"], page.Markets.Select(m => m.Ticker));
        Assert.Equal("closed", page.Markets[1].Status);
        Assert.Null(page.NextCursor);
        Assert.Equal(ApiFormat.Moment(NowUtc.AddHours(-5)), page.Markets[0].OpenTime);
    }

    [Fact]
    public async Task MarketFiltersAndTheLimitReachTheReaderAndTheNextPageGetsACursor()
    {
        Fakes.Markets.Page = new Page<StoredMarket, MarketPosition>([Market("A-1")], new MarketPosition("A-1"));

        var page = await _api.GetAsync<MarketPage>("/v1/markets?status=closed&event=KXHIGH-26OCT19&q=NY&limit=5");

        var (filter, _, limit) = Fakes.Markets.Lists.Single();
        Assert.Equal((new MarketFilter("closed", "KXHIGH-26OCT19", "NY"), 5), (filter, limit));
        Assert.Equal("A-1", ApiFormat.DecodeCursor(page.NextCursor!));
    }

    [Fact]
    public async Task ACursorFromThePreviousPageResumesAfterItsTicker()
    {
        await _api.GetAsync<MarketPage>("/v1/markets?cursor=" + ApiFormat.EncodeCursor("A-1"));

        Assert.Equal(new MarketPosition("A-1"), Fakes.Markets.Lists.Single().After);
    }

    [Theory]
    [InlineData("/v1/markets?limit=0", "limit")]
    [InlineData("/v1/markets?limit=501", "limit")]
    [InlineData("/v1/markets?limit=abc", "limit")]
    [InlineData("/v1/markets?limit=-1", "limit")]
    [InlineData("/v1/markets?limit=1&limit=2", "limit")]
    [InlineData("/v1/markets?status=Active", "status")]
    [InlineData("/v1/markets?status=a%20b", "status")]
    [InlineData("/v1/markets?event=has%20space", "event")]
    [InlineData("/v1/markets?q=a", "q")]
    [InlineData("/v1/markets?q=ab%00", "q")]
    [InlineData("/v1/markets?cursor=!!!", "cursor")]
    public async Task BadMarketParametersAreNamedAndRefused(string url, string field)
    {
        await AssertInvalidAsync(url, field);

        Assert.Empty(Fakes.Markets.Lists);
    }

    [Fact]
    public async Task EveryBadParameterIsNamedNeverItsValue()
    {
        var (_, body) = await _api.GetAsync("/v1/markets?limit=999&status=BAD%21SECRET");

        Assert.Equal(["limit", "status"], ApiJson.Deserialize<ErrorDto>(body)!.Fields);
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACursorThatIsNotATickerIsRefused()
    {
        await AssertInvalidAsync("/v1/markets?cursor=" + ApiFormat.EncodeCursor("not a ticker!"), "cursor");
    }

    [Fact]
    public async Task OneMarketIsFoundOrNotFoundOrRefused()
    {
        Fakes.Markets.ByTicker["KX-1"] = Market("KX-1");

        var found = await _api.GetAsync<MarketDto>("/v1/markets/KX-1");
        var (missing, body) = await _api.GetAsync("/v1/markets/KX-2");

        Assert.Equal("KX-1", found.Ticker);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", ApiJson.Deserialize<ErrorDto>(body)!.Error);
        await AssertInvalidAsync("/v1/markets/bad%20ticker", "ticker");
    }

    // ---- ticks ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TicksWithoutParametersAskForTheLatestRowsUpToJustAfterNow()
    {
        Fakes.Ticks.Page = new Page<StoredTick, TickPosition>([Tick(NowUtc.AddSeconds(-2)), Tick(NowUtc.AddSeconds(-1), 570_000)], null);

        var page = await _api.GetAsync<TickPage>("/v1/markets/KX-1/ticks");

        var read = Assert.Single(Fakes.Ticks.Reads);
        Assert.Equal(("KX-1", (DateTime?)null, NowUtc.AddSeconds(1), (TickPosition?)null, 200), read);
        Assert.Equal(["0.560000", "0.570000"], page.Ticks.Select(t => t.Price));
        Assert.Equal("10.00", page.Ticks[0].YesBidSize);
        Assert.Null(page.Ticks[0].LastTradeSize);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task TickTimesWithAnyOffsetAreConvertedToUtc()
    {
        await _api.GetAsync<TickPage>("/v1/markets/KX-1/ticks?start=2026-10-18T10:00:00-06:00&end=2026-10-18T20:00:00Z&limit=1000");

        var read = Fakes.Ticks.Reads.Single();
        Assert.Equal((new DateTime(2026, 10, 18, 16, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc), 1000), (read.Start!.Value, read.End, read.Limit));
    }

    [Theory]
    [InlineData("start=2026-10-18T10:00:00", "start")]                  // no offset: ambiguous
    [InlineData("end=yesterday", "end")]
    [InlineData("start=2026-13-45T00:00:00Z", "start")]
    [InlineData("limit=1001", "limit")]
    [InlineData("start=2026-10-18T12:00:00Z&end=2026-10-18T12:00:00Z", "start,end")]   // empty window
    [InlineData("start=2026-10-19T12:00:00Z&end=2026-10-18T12:00:00Z", "start,end")]
    [InlineData("cursor=" + "e", "cursor")]
    public async Task BadTickParametersAreNamedAndRefused(string query, string fields)
    {
        await AssertInvalidAsync("/v1/markets/KX-1/ticks?" + query, fields.Split(','));

        Assert.Empty(Fakes.Ticks.Reads);
    }

    [Fact]
    public async Task ATickCursorCarriesTheTimeAndHowManyRowsAtItWereAlreadySent()
    {
        var at = new DateTime(2026, 10, 18, 22, 0, 1, 500, DateTimeKind.Utc);
        Fakes.Ticks.Page = new Page<StoredTick, TickPosition>([Tick(at)], new TickPosition(at, 3));

        var page = await _api.GetAsync<TickPage>("/v1/markets/KX-1/ticks?start=2026-10-18T22:00:00Z&limit=1");

        Assert.Equal("2026-10-18T22:00:01.500000+00:00|3", ApiFormat.DecodeCursor(page.NextCursor!));

        await _api.GetAsync<TickPage>("/v1/markets/KX-1/ticks?cursor=" + page.NextCursor);

        Assert.Equal(new TickPosition(at, 3), Fakes.Ticks.Reads[1].After);
    }

    [Theory]
    [InlineData("2026-10-18T22:00:01.500000+00:00")]            // no skip
    [InlineData("2026-10-18T22:00:01.500000+00:00|x")]
    [InlineData("2026-10-18T22:00:01.500000+00:00|-1")]
    [InlineData("2026-10-18T22:00:01.500000+00:00|1000001")]
    [InlineData("not a time|1")]
    [InlineData("2026-10-18T22:00:01.500000|1")]                // no offset
    public async Task ATickCursorThatIsNotOursIsRefused(string inside)
    {
        await AssertInvalidAsync("/v1/markets/KX-1/ticks?cursor=" + ApiFormat.EncodeCursor(inside), "cursor");
    }

    [Fact]
    public async Task TicksOfAnUnknownMarketAreNotFoundAndABadTickerIsRefused()
    {
        Fakes.Ticks.Page = null;

        var (response, _) = await _api.GetAsync("/v1/markets/KX-NOPE/ticks");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertInvalidAsync("/v1/markets/bad%20ticker/ticks", "ticker");
    }

    // ---- trades ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TradesMapTheSideTheBlockFlagAndBothPrices()
    {
        Fakes.Trades.Page = new Page<StoredTrade, TradePosition>([Trade(NowUtc, "t-1", 1), Trade(NowUtc, "t-2", 2, true), Trade(NowUtc, "t-3", 0)], null);

        var page = await _api.GetAsync<TradePage>("/v1/markets/KX-1/trades");

        Assert.Equal(["yes", "no", null], page.Trades.Select(t => t.TakerSide));
        Assert.Equal([false, true, false], page.Trades.Select(t => t.IsBlockTrade));
        Assert.Equal(("0.400000", "0.600000", "3.00"), (page.Trades[0].YesPrice, page.Trades[0].NoPrice, page.Trades[0].Count));
        Assert.Equal(("KX-1", (DateTime?)null, NowUtc.AddSeconds(1), (TradePosition?)null, 200), Fakes.Trades.Reads.Single());
    }

    [Fact]
    public async Task ATradeCursorCarriesTheTimeAndTheIdAndASecondPageResumesAfterIt()
    {
        Fakes.Trades.Page = new Page<StoredTrade, TradePosition>([Trade(NowUtc, "t-9")], new TradePosition(NowUtc, "t-9"));

        var page = await _api.GetAsync<TradePage>("/v1/markets/KX-1/trades?start=2026-10-18T00:00:00Z&limit=1");
        await _api.GetAsync<TradePage>("/v1/markets/KX-1/trades?cursor=" + page.NextCursor);

        Assert.Equal(ApiFormat.Moment(NowUtc) + "|t-9", ApiFormat.DecodeCursor(page.NextCursor!));
        Assert.Equal(new TradePosition(NowUtc, "t-9"), Fakes.Trades.Reads[1].After);
    }

    [Theory]
    [InlineData("2026-10-18T22:00:01.500000+00:00")]            // no id
    [InlineData("|t-1")]
    [InlineData("2026-10-18T22:00:01.500000+00:00|bad id")]
    [InlineData("2026-10-18T22:00:01.500000+00:00|")]
    [InlineData("nope|t-1")]
    public async Task ATradeCursorThatIsNotOursIsRefused(string inside)
    {
        await AssertInvalidAsync("/v1/markets/KX-1/trades?cursor=" + ApiFormat.EncodeCursor(inside), "cursor");
    }

    [Fact]
    public async Task TradesOfAnUnknownMarketAreNotFoundAndABadWindowIsRefused()
    {
        Fakes.Trades.Page = null;

        var (response, _) = await _api.GetAsync("/v1/markets/KX-NOPE/trades");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertInvalidAsync("/v1/markets/KX-1/trades?start=2026-10-19T00:00:00Z&end=2026-10-18T00:00:00Z", "start", "end");
        await AssertInvalidAsync("/v1/markets/bad%20ticker/trades", "ticker");
    }

    // ---- metrics --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MetricsAreComputedAsOfNowAndMapEveryFigure()
    {
        var metrics = await _api.GetAsync<MetricsDto>("/v1/markets/KX-1/metrics");

        Assert.Equal(("KX-1", ApiFormat.Moment(NowUtc), "0.560000", "0.020000"), (metrics.Ticker, metrics.AsOf, metrics.Last, metrics.Change24h));
        Assert.Equal(("4.00", 2L, "99.00", "0.020000", "0.550000"), (metrics.Volume24h, metrics.TradeCount24h, metrics.OpenInterest, metrics.Spread, metrics.Vwap24h));
        Assert.Equal((null, null, "0.106066", false), (metrics.Sma20, metrics.Sma50, metrics.Volatility24h, metrics.Partial));
        Assert.Equal(("KX-1", NowUtc), Fakes.Metrics.Reads.Single());
    }

    [Fact]
    public async Task MetricsOfAnUnknownMarketAreNotFoundAndABadTickerIsRefused()
    {
        Fakes.Metrics.Inputs = null;

        var (response, _) = await _api.GetAsync("/v1/markets/KX-NOPE/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertInvalidAsync("/v1/markets/bad%20ticker/metrics", "ticker");
    }

    // ---- candles --------------------------------------------------------------------------------------------------------------------

    private static StoredCandle Candle(DateTime start, bool partial = false) =>
        new(start, 3600, 400_000, 550_000, 390_000, 450_000, 40_000, 3, 440_000, 460_000, 12_300, partial, false, partial ? "ws_lost" : null, partial ? 45 : null, 7);

    [Fact]
    public async Task CandlesAreMappedAndTheQueryDefaultsToTheLatestHourlyOnes()
    {
        Fakes.Candles.Result = new CandleResult([Candle(NowUtc.AddHours(-2)), Candle(NowUtc.AddHours(-1), partial: true)], "stored 1h", null);

        var page = await _api.GetAsync<CandlePage>("/v1/markets/KX-1/candles");

        Assert.Equal(("1h", "stored 1h", null), (page.Interval, page.Source, page.NextCursor));
        var first = page.Candles[0];
        Assert.Equal(("0.400000", "0.550000", "0.390000", "0.450000", "400.00", 3), (first.Open, first.High, first.Low, first.Close, first.Volume, first.TradeCount));
        Assert.Equal(("0.440000", "0.460000", "123.00", false, null), (first.YesBid, first.YesAsk, first.OpenInterest, first.Partial, first.GapKind));
        Assert.Equal((true, true, false, "ws_lost", 45), (page.Candles[1].Partial, page.Candles[1].TradesPartial, page.Candles[1].BookPartial, page.Candles[1].GapKind, page.Candles[1].GapSeconds));
        Assert.Equal(new CandleQuery("KX-1", 3600, null, NowUtc.AddSeconds(1), 200), Fakes.Candles.Queries.Single());
    }

    [Theory]
    [InlineData("15m", 900)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    [InlineData("1w", 604800)]
    [InlineData("90", 5400)]
    public async Task TheIntervalIsParsedInUnitsOrBareMinutes(string interval, int seconds)
    {
        await _api.GetAsync<CandlePage>("/v1/markets/KX-1/candles?interval=" + interval);

        Assert.Equal(seconds, Fakes.Candles.Queries.Single().IntervalSeconds);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("30s")]
    [InlineData("2d")]
    [InlineData("1500")]
    [InlineData("abc")]
    public async Task AnIntervalThatCannotBeServedIsRefused(string interval)
    {
        await AssertInvalidAsync("/v1/markets/KX-1/candles?interval=" + interval, "interval");
    }

    [Fact]
    public async Task ACandleCursorIsTheNextBucketAndASecondPageStartsThere()
    {
        Fakes.Candles.Result = new CandleResult([Candle(NowUtc.AddHours(-5))], "stored 1h", NowUtc.AddHours(-4));

        var page = await _api.GetAsync<CandlePage>("/v1/markets/KX-1/candles?start=2026-10-18T00:00:00Z&limit=1");
        await _api.GetAsync<CandlePage>("/v1/markets/KX-1/candles?cursor=" + page.NextCursor);

        Assert.Equal(ApiFormat.Moment(NowUtc.AddHours(-4)), ApiFormat.DecodeCursor(page.NextCursor!));
        Assert.Equal(NowUtc.AddHours(-4), Fakes.Candles.Queries[1].Start);
    }

    [Fact]
    public async Task CandlesOfAnUnknownMarketAreNotFoundAndBadParametersAreRefused()
    {
        Fakes.Candles.Result = null;

        var (response, _) = await _api.GetAsync("/v1/markets/KX-NOPE/candles");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertInvalidAsync("/v1/markets/KX-1/candles?start=2026-10-19T00:00:00Z&end=2026-10-18T00:00:00Z", "start", "end");
        await AssertInvalidAsync("/v1/markets/bad%20ticker/candles", "ticker");
        await AssertInvalidAsync("/v1/markets/KX-1/candles?cursor=" + ApiFormat.EncodeCursor("nope"), "cursor");
    }

    [Fact]
    public async Task ARangeTooLongToComputeIsRefusedNamingTheParametersToChange()
    {
        Fakes.Candles.Throw = new CandleRangeException();

        await AssertInvalidAsync("/v1/markets/KX-1/candles?interval=7m", "start", "end", "interval");
    }

    // ---- gaps -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GapsDefaultToTheLastWeekAndMapOpenGaps()
    {
        Fakes.Gaps.Gaps = [new StoredGap(2, "ws_lost", NowUtc.AddMinutes(-5), null, "still open"), new StoredGap(1, "ingest_down", NowUtc.AddHours(-2), NowUtc.AddHours(-1), null)];

        var page = await _api.GetAsync<GapPage>("/v1/gaps");

        Assert.Equal((NowUtc.AddDays(-7), NowUtc.AddSeconds(1), 100), Fakes.Gaps.Reads.Single());
        Assert.Equal([2L, 1L], page.Gaps.Select(g => g.Id));
        Assert.Null(page.Gaps[0].To);
        Assert.Equal("still open", page.Gaps[0].Detail);
        Assert.Equal(ApiFormat.Moment(NowUtc.AddHours(-2)), page.Gaps[1].From);
    }

    [Theory]
    [InlineData("start=nope", "start")]
    [InlineData("end=2026-10-18T00:00:00", "end")]
    [InlineData("limit=0", "limit")]
    [InlineData("start=2026-10-19T00:00:00Z&end=2026-10-18T00:00:00Z", "start,end")]
    public async Task BadGapParametersAreNamedAndRefused(string query, string fields)
    {
        await AssertInvalidAsync("/v1/gaps?" + query, fields.Split(','));

        Assert.Empty(Fakes.Gaps.Reads);
    }

    // ---- status ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StatusIsBuiltFromTheSnapshotAtTheCurrentTime()
    {
        Fakes.Status.Snapshot = new StatusSnapshot(new Data.State.IngestStateEntry("running", NowUtc.AddSeconds(-5)), NowUtc.AddSeconds(-1), NowUtc.AddSeconds(-2), 142_606_336, 107_374_182_400, 900_000_000_000, new Dictionary<string, long> { ["active"] = 3 }, new Dictionary<string, long> { ["ingest_down"] = 2 });

        var status = await _api.GetAsync<StatusDto>("/v1/status");

        Assert.Equal(NowUtc, Fakes.Status.AskedAt);
        Assert.Empty(status.Problems);
        Assert.Equal(("running", 5L), (status.Ingest.State, status.Ingest.HeartbeatAgeSeconds));
        Assert.Equal(3, status.Markets["active"]);
        Assert.Equal(2, status.Gaps.Count);
    }

    // ---- everything else ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownRouteAndAWrongMethodGetTheSameErrorShape()
    {
        var (missing, missingBody) = await _api.GetAsync("/v1/nothing");
        var post = await _api.Client.PostAsync(new Uri("/v1/markets", UriKind.Relative), new StringContent("{}"), TestContext.Current.CancellationToken);
        var delete = await _api.Client.DeleteAsync(new Uri("/v1/markets/KX-1", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", ApiJson.Deserialize<ErrorDto>(missingBody)!.Error);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal("method_not_allowed", ApiJson.Deserialize<ErrorDto>(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.Error);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task EveryResponseIsJsonNotCachedAndNotSniffed()
    {
        var (ok, _) = await _api.GetAsync("/healthz");
        var (error, _) = await _api.GetAsync("/v1/nothing");

        foreach (var response in new[] { ok, error })
        {
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Fact]
    public async Task ADatabaseThatTimesOutIsUnavailableAndABugIsAnInternalErrorNeitherSayingWhy()
    {
        Fakes.Markets.Throw = new TimeoutException("the database said SECRET-DETAIL");
        var (timeout, timeoutBody) = await _api.GetAsync("/v1/markets");

        Fakes.Markets.Throw = new InvalidOperationException("a bug with SECRET-DETAIL");
        var (bug, bugBody) = await _api.GetAsync("/v1/markets");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, timeout.StatusCode);
        Assert.Equal("unavailable", ApiJson.Deserialize<ErrorDto>(timeoutBody)!.Error);
        Assert.Equal(HttpStatusCode.InternalServerError, bug.StatusCode);
        Assert.Equal("internal_error", ApiJson.Deserialize<ErrorDto>(bugBody)!.Error);
        Assert.DoesNotContain("SECRET", timeoutBody + bugBody, StringComparison.Ordinal);
    }

    // ---- order books ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrderBooksAreListedWithTheirFlagsAgeAndTheDefaultDepth()
    {
        Fakes.Books.Books.Add(Samples.Book("A-1", levels: 15));
        Fakes.Books.Books.Add(Samples.Book("B-2", stale: true, approximate: true, levels: 1));

        var page = await _api.GetAsync<OrderBookPage>("/v1/orderbooks");

        Assert.Equal(["A-1", "B-2"], page.Books.Select(b => b.Ticker));
        var first = page.Books[0];
        Assert.Equal((10, 10), (first.YesBids.Count, first.NoBids.Count));
        Assert.Equal(("0.410000", "1.50"), (first.YesBids[0].Price, first.YesBids[0].Quantity));
        Assert.Equal(("0.550000", "2.00"), (first.NoBids[0].Price, first.NoBids[0].Quantity));
        Assert.Equal((2L, false, false), (first.AgeSeconds, first.Stale, first.Approximate));
        Assert.Equal((true, true), (page.Books[1].Stale, page.Books[1].Approximate));
    }

    [Fact]
    public async Task OneOrderBookIsCutToTheDepthAskedFor()
    {
        Fakes.Books.Books.Add(Samples.Book("A-1", levels: 15));

        var book = await _api.GetAsync<OrderBookDto>("/v1/markets/A-1/orderbook?depth=3");
        var deepest = await _api.GetAsync<OrderBookDto>("/v1/markets/A-1/orderbook?depth=100");

        Assert.Equal((3, 3), (book.YesBids.Count, book.NoBids.Count));
        Assert.Equal((15, 15), (deepest.YesBids.Count, deepest.NoBids.Count));
        Assert.Equal("2026-10-19T12:00:00.000000+00:00".Length, book.UpdatedAt.Length);
    }

    [Fact]
    public async Task AnUnwatchedMarketHasNoOrderBook()
    {
        var (response, body) = await _api.GetAsync("/v1/markets/NOPE-1/orderbook");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", ApiJson.Deserialize<ErrorDto>(body)!.Error);
    }

    [Theory]
    [InlineData("/v1/orderbooks?depth=0", "depth")]
    [InlineData("/v1/orderbooks?depth=101", "depth")]
    [InlineData("/v1/orderbooks?depth=abc", "depth")]
    [InlineData("/v1/orderbooks?depth=2&depth=3", "depth")]
    [InlineData("/v1/markets/A-1/orderbook?depth=-1", "depth")]
    [InlineData("/v1/markets/bad%20ticker/orderbook", "ticker")]
    public async Task BadOrderBookParametersAreNamedNotEchoed(string url, string field)
    {
        await AssertInvalidAsync(url, field);
    }
}
