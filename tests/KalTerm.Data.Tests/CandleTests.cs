using System.Globalization;
using KalTerm.Data.Candles;
using KalTerm.Data.Gaps;
using KalTerm.Data.State;

namespace KalTerm.Data.Tests;

public class CandleRulesTests
{
    [Fact]
    public void TheDefaultIsDaysAndHoursForEveryMarketAndMinutesForTheWatchList()
    {
        Assert.Equal(["1d=all", "1h=all", "1m=watched"], CandleRules.Default.Select(r => r.ToString()));
        Assert.Equal(CandleRules.DefaultText, CandleRules.Format(CandleRules.Default));
    }

    [Fact]
    public void RulesAreReadWhateverTheirOrderAndWrittenCoarsestFirst()
    {
        var rules = CandleRules.Parse(" 5m = Watched ; 1H=all;1d=ALL ");

        Assert.Equal([(86400, CandleScope.All), (3600, CandleScope.All), (300, CandleScope.Watched)], rules.Select(r => (r.IntervalSeconds, r.Scope)));
        Assert.Equal("1d=all;1h=all;5m=watched", CandleRules.Format(rules));
        Assert.Empty(CandleRules.Parse(null));
        Assert.Empty(CandleRules.Parse(" ; "));
        Assert.Throws<ArgumentNullException>(() => CandleRules.Format(null!));
    }

    [Theory]
    [InlineData("7m=all")]
    [InlineData("1w=all")]
    [InlineData("1h")]
    [InlineData("1h=sometimes")]
    [InlineData("1h=all;60m=all")]
    [InlineData("=all")]
    [InlineData("abc=all")]
    public void ARuleThatCannotBeStoredOrIsAmbiguousIsRefused(string text)
    {
        Assert.Throws<FormatException>(() => CandleRules.Parse(text));
    }

    [Theory]
    [InlineData("1m", 60)]
    [InlineData("15m", 900)]
    [InlineData("90", 5400)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    [InlineData("1w", 604800)]
    [InlineData(" 2H ", 7200)]
    [InlineData("1440", 86400)]
    public void IntervalsAreReadAsMinutesHoursDaysOrOneWeek(string text, int seconds)
    {
        Assert.True(CandleRules.TryParseInterval(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("2w")]
    [InlineData("8d")]
    [InlineData("10s")]
    [InlineData("999999999m")]
    [InlineData("m")]
    public void NonsenseOrTooLongIntervalsAreRefused(string? text)
    {
        Assert.False(CandleRules.TryParseInterval(text, out _));
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(5400, true)]
    [InlineData(86400, true)]
    [InlineData(604800, true)]
    [InlineData(30, false)]
    [InlineData(90, false)]
    [InlineData(86460, false)]
    [InlineData(172800, false)]
    public void OnlyWholeMinutesUpToADayOrExactlyAWeekMayBeRequested(int seconds, bool allowed)
    {
        Assert.Equal(allowed, CandleRules.IsRequestable(seconds));
    }

    [Theory]
    [InlineData(60, "1m")]
    [InlineData(900, "15m")]
    [InlineData(3600, "1h")]
    [InlineData(14400, "4h")]
    [InlineData(86400, "1d")]
    [InlineData(604800, "1w")]
    [InlineData(5400, "90m")]
    public void IntervalsAreWrittenInTheirShortestExactForm(int seconds, string text)
    {
        Assert.Equal(text, CandleRules.IntervalText(seconds));
    }

    [Fact]
    public void BucketsStartAtTheEpochExceptAWeekWhichStartsOnAMonday()
    {
        var moment = new DateTime(2026, 10, 21, 13, 47, 31, DateTimeKind.Utc);   // a Wednesday

        Assert.Equal(new DateTime(2026, 10, 21, 13, 47, 0, DateTimeKind.Utc), CandleRules.BucketStart(moment, 60));
        Assert.Equal(new DateTime(2026, 10, 21, 13, 45, 0, DateTimeKind.Utc), CandleRules.BucketStart(moment, 900));
        Assert.Equal(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc), CandleRules.BucketStart(moment, 14400));
        Assert.Equal(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), CandleRules.BucketStart(moment, 86400));
        var week = CandleRules.BucketStart(moment, CandleRules.WeekSeconds);
        Assert.Equal(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), week);
        Assert.Equal(DayOfWeek.Monday, week.DayOfWeek);
    }
}

public class CandleRollupTests
{
    private static readonly DateTime Hour = new(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);

    private static StoredCandle Minute(int minute, long? open, long? high, long? low, long? close, long volume, int trades, long? bid = null, long? ask = null, long? interest = null, bool tradesPartial = false, bool bookPartial = false, string? kind = null, int? seconds = null) =>
        new(Hour.AddMinutes(minute), 60, open, high, low, close, volume, trades, bid, ask, interest, tradesPartial, bookPartial, kind, seconds, 7);

    [Fact]
    public void FiveMinuteCandlesBecomeOneWithTheFirstOpenTheLastCloseAndTheExtremes()
    {
        var rolled = CandleRollup.Roll(
        [
            Minute(0, 400_000, 410_000, 395_000, 405_000, 200, 2, 400_000, 410_000, 1_000),
            Minute(1, null, null, null, null, 0, 0, 401_000, 411_000, 1_010),   // ticks only: no price
            Minute(2, 406_000, 450_000, 406_000, 440_000, 300, 3, 439_000, 441_000, 1_100),
            Minute(4, 430_000, 432_000, 300_000, 310_000, 100, 1, null, null, null),
            Minute(5, 310_000, 320_000, 310_000, 320_000, 50, 1, 319_000, 321_000, 1_200),
        ], 300);

        Assert.Equal(2, rolled.Count);
        var first = rolled[0];
        Assert.Equal((Hour, 300, 400_000L, 450_000L, 300_000L, 310_000L, 600L, 6), (first.BucketStart, first.IntervalSeconds, first.OpenE6, first.HighE6, first.LowE6, first.CloseE6, first.VolumeE2, first.TradeCount));
        Assert.Equal((439_000L, 441_000L, 1_100L), (first.YesBidE6, first.YesAskE6, first.OpenInterestE2));   // the last candle that had them
        Assert.Equal(Hour.AddMinutes(5), rolled[1].BucketStart);
        Assert.Equal(7, first.MarketId);
    }

    [Fact]
    public void AGapFlagInAnyMinuteMarksTheWholeCandleAndTheGapSecondsAdd()
    {
        var rolled = CandleRollup.Roll(
        [
            Minute(0, 1, 1, 1, 1, 1, 1),
            Minute(1, 1, 1, 1, 1, 1, 1, tradesPartial: true, bookPartial: true, kind: "ws_reconnected", seconds: 5),
            Minute(2, 1, 1, 1, 1, 1, 1, bookPartial: true, kind: "ingest_down", seconds: 40),
        ], 300);

        var candle = Assert.Single(rolled);
        Assert.Equal((true, true, true, "ingest_down", 45), (candle.TradesPartial, candle.BookPartial, candle.Partial, candle.GapKind, candle.GapSeconds));
    }

    [Fact]
    public void AnIntervalWithNoTradesHasNoPriceAndNoGapMeansNoFlags()
    {
        var rolled = CandleRollup.Roll([Minute(0, null, null, null, null, 0, 0, 400_000, 410_000, 5), Minute(1, null, null, null, null, 0, 0)], 300);

        var candle = Assert.Single(rolled);
        Assert.Equal((null, null, null, null, 0L, 0, false, null, null), (candle.OpenE6, candle.HighE6, candle.LowE6, candle.CloseE6, candle.VolumeE2, candle.TradeCount, candle.Partial, candle.GapKind, candle.GapSeconds));
        Assert.Equal(400_000L, candle.YesBidE6);
        Assert.Empty(CandleRollup.Roll([], 300));
        Assert.Throws<ArgumentNullException>(() => CandleRollup.Roll(null!, 300));
    }

    [Fact]
    public void RollingUpToADayAndAWeekAlignsToUtcDaysAndMondays()
    {
        var monday = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var days = Enumerable.Range(0, 9).Select(d => new StoredCandle(monday.AddDays(d - 1), 86400, 1, 2, 1, 2, 100, 1, null, null, null, false, false, null, null, 7)).ToList();   // Sunday the 18th to Monday the 26th

        var weeks = CandleRollup.Roll(days, CandleRules.WeekSeconds);

        Assert.Equal([monday.AddDays(-7), monday, monday.AddDays(7)], weeks.Select(w => w.BucketStart));
        Assert.Equal([100L, 700L, 100L], weeks.Select(w => w.VolumeE2));
    }
}

public class CandleBuilderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Ten = new(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc);

    private CandleBuilder Builder(int pass = CandleBuilder.MarketsPerPass) => new(new DataSource(db.Options), pass);

    private CandleReader Reader() => new(new DataSource(db.Options));

    private Task TradeAsync(int market, DateTime at, string id, long price, long count) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES ('{ReaderSeed.Time(at)}', {market}, '{id}', {price}, {1_000_000 - price}, {count}, 1, 0)"));

    private Task TickAsync(int market, DateTime at, long price, long bid, long ask, long interest) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, open_interest_e2, volume_e2) VALUES ('{ReaderSeed.Time(at)}', {market}, {price}, {bid}, {ask}, {interest}, 1)"));

    private async Task<StoredCandle> OneAsync(int market, int interval, DateTime start)
    {
        var rows = await Reader().ReadAsync(market, interval, start, start.AddSeconds(interval), 10, TestContext.Current.CancellationToken);
        return Assert.Single(rows);
    }

    [Fact]
    public async Task AnHourOfTradesBecomesOneCandleWithTheFirstAndLastByTimeAndTheLastTick()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-OHLC");
        await TradeAsync(m, Ten.AddMinutes(5), "a", 400_000, 200);
        await TradeAsync(m, Ten.AddMinutes(20), "b", 550_000, 100);
        await TradeAsync(m, Ten.AddMinutes(20), "c", 540_000, 50);   // same time as b: ordered by trade id
        await TradeAsync(m, Ten.AddMinutes(40), "d", 350_000, 300);
        await TradeAsync(m, Ten.AddMinutes(59).AddSeconds(59), "e", 450_000, 100);
        await TradeAsync(m, Ten.AddHours(1), "f", 999_000, 999);   // the next bucket
        await TickAsync(m, Ten.AddMinutes(1), 400_000, 390_000, 410_000, 1_000);
        await TickAsync(m, Ten.AddMinutes(58), 450_000, 440_000, 460_000, 1_500);
        await TickAsync(m, Ten.AddMinutes(30), 500_000, 1, 2, 3);

        var written = await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);

        var candle = await OneAsync(m, 3600, Ten);
        Assert.Equal(1, written);
        Assert.Equal((400_000L, 550_000L, 350_000L, 450_000L, 750L, 5), (candle.OpenE6, candle.HighE6, candle.LowE6, candle.CloseE6, candle.VolumeE2, candle.TradeCount));
        Assert.Equal((440_000L, 460_000L, 1_500L), (candle.YesBidE6, candle.YesAskE6, candle.OpenInterestE2));
        Assert.False(candle.Partial);
        Assert.Equal((Ten, DateTimeKind.Utc, m), (candle.BucketStart, candle.BucketStart.Kind, candle.MarketId));
    }

    [Fact]
    public async Task ABucketWithOnlyTicksHasNoPriceAndOneWithOnlyTradesHasNoBook()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-SPARSE");
        await TickAsync(m, Ten.AddMinutes(1), 400_000, 390_000, 410_000, 7);
        await TradeAsync(m, Ten.AddHours(1).AddMinutes(1), "t1", 600_000, 100);

        await Builder().BuildAsync(3600, Ten, Ten.AddHours(2), [m], Now, TestContext.Current.CancellationToken);

        var ticksOnly = await OneAsync(m, 3600, Ten);
        var tradesOnly = await OneAsync(m, 3600, Ten.AddHours(1));
        Assert.Equal((null, null, 0L, 0, 390_000L, 7L), (ticksOnly.OpenE6, ticksOnly.CloseE6, ticksOnly.VolumeE2, ticksOnly.TradeCount, ticksOnly.YesBidE6, ticksOnly.OpenInterestE2));
        Assert.Equal((600_000L, 600_000L, 100L, 1, null, null), (tradesOnly.OpenE6, tradesOnly.CloseE6, tradesOnly.VolumeE2, tradesOnly.TradeCount, tradesOnly.YesBidE6, tradesOnly.OpenInterestE2));
    }

    [Fact]
    public async Task BuildingAgainReplacesTheRowsSoItNeverDuplicatesAndPicksUpNewData()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-AGAIN");
        await TradeAsync(m, Ten.AddMinutes(5), "a", 400_000, 100);
        await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);
        await TradeAsync(m, Ten.AddMinutes(50), "b", 500_000, 100);

        var again = await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);

        var candle = await OneAsync(m, 3600, Ten);
        Assert.Equal(1, again);
        Assert.Equal((400_000L, 500_000L, 200L, 2), (candle.OpenE6, candle.CloseE6, candle.VolumeE2, candle.TradeCount));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM dbo.candle WHERE market_id = {m} AND interval_s = 3600"));
    }

    [Fact]
    public async Task EachIntervalIsBuiltFromTheRawRowsOnItsOwnSoAMinuteAndAnHourAgree()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-INTERVALS");
        await TradeAsync(m, Ten.AddMinutes(1), "a", 400_000, 100);
        await TradeAsync(m, Ten.AddMinutes(2), "b", 420_000, 200);
        await TradeAsync(m, Ten.AddMinutes(31), "c", 380_000, 300);
        await Builder().BuildAsync(60, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);
        await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);

        var minutes = await Reader().ReadAsync(m, 60, Ten, Ten.AddHours(1), 100, TestContext.Current.CancellationToken);
        var rolled = Assert.Single(CandleRollup.Roll(minutes, 3600));
        var stored = await OneAsync(m, 3600, Ten);

        Assert.Equal(3, minutes.Count);
        Assert.Equal((stored.OpenE6, stored.HighE6, stored.LowE6, stored.CloseE6, stored.VolumeE2, stored.TradeCount), (rolled.OpenE6, rolled.HighE6, rolled.LowE6, rolled.CloseE6, rolled.VolumeE2, rolled.TradeCount));
    }

    [Fact]
    public async Task AGapFlagsTheBucketsItOverlapsAndTheTradeFlagClearsWhenItsBackfillIsDone()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-GAPS");
        await TradeAsync(m, Ten.AddMinutes(5), "a", 400_000, 100);
        await TradeAsync(m, Ten.AddHours(1).AddMinutes(5), "b", 400_000, 100);
        await TradeAsync(m, Ten.AddHours(2).AddMinutes(5), "c", 400_000, 100);
        await db.ExecuteAsync("DELETE FROM dbo.ingest_gap");
        await new GapStore(new DataSource(db.Options)).AppendAsync(
        [
            new GapRecord(GapKinds.IngestDown, Ten.AddMinutes(10), Ten.AddMinutes(30), "long"),
            new GapRecord(GapKinds.WsReconnected, Ten.AddMinutes(40), Ten.AddMinutes(40).AddSeconds(5), "short"),
            new GapRecord(GapKinds.WsLost, Ten.AddHours(1).AddMinutes(50), Ten.AddHours(2).AddMinutes(10), "straddles two buckets"),
            new GapRecord(GapKinds.IngestDown, Ten.AddMinutes(20), Ten.AddMinutes(25), "another market's", MarketId: m + 1000),
        ], TestContext.Current.CancellationToken);

        await Builder().BuildAsync(3600, Ten, Ten.AddHours(3), [m], Now, TestContext.Current.CancellationToken);

        var first = await OneAsync(m, 3600, Ten);
        var second = await OneAsync(m, 3600, Ten.AddHours(1));
        var third = await OneAsync(m, 3600, Ten.AddHours(2));
        Assert.Equal((true, true, GapKinds.IngestDown, 1205), (first.TradesPartial, first.BookPartial, first.GapKind, first.GapSeconds));   // 20 minutes and 5 seconds; the other market's gap does not count
        Assert.Equal((true, true, GapKinds.WsLost, 600), (second.TradesPartial, second.BookPartial, second.GapKind, second.GapSeconds));
        Assert.Equal((true, true, GapKinds.WsLost, 600), (third.TradesPartial, third.BookPartial, third.GapKind, third.GapSeconds));
        await db.ExecuteAsync($"UPDATE dbo.ingest_gap SET backfill_status = 'done' WHERE detail IN ('long', 'short')");

        await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [m], Now, TestContext.Current.CancellationToken);

        var recovered = await OneAsync(m, 3600, Ten);
        Assert.Equal((false, true, 1205), (recovered.TradesPartial, recovered.BookPartial, recovered.GapSeconds));   // trades are whole again; the ticker side never is
    }

    [Fact]
    public async Task ABucketAGapDoesNotTouchIsNotFlaggedAndAnOpenGapRunsUntilNow()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-OPENGAP");
        await TradeAsync(m, Ten.AddMinutes(5), "a", 400_000, 100);
        await TradeAsync(m, Ten.AddHours(1).AddMinutes(5), "b", 400_000, 100);
        await db.ExecuteAsync("DELETE FROM dbo.ingest_gap");
        await new GapStore(new DataSource(db.Options)).AppendAsync([new GapRecord(GapKinds.IngestDown, Ten.AddHours(1).AddMinutes(30), null, "still open")], TestContext.Current.CancellationToken);

        await Builder().BuildAsync(3600, Ten, Ten.AddHours(2), [m], Ten.AddHours(1).AddMinutes(45), TestContext.Current.CancellationToken);

        var clean = await OneAsync(m, 3600, Ten);
        var open = await OneAsync(m, 3600, Ten.AddHours(1));
        Assert.Equal((false, false, null, null), (clean.TradesPartial, clean.BookPartial, clean.GapKind, clean.GapSeconds));
        Assert.Equal((true, true, 900), (open.TradesPartial, open.BookPartial, open.GapSeconds));
    }

    [Fact]
    public async Task OnlyTheRequestedMarketsAreBuiltAndEverySliceOfMarketsGivesTheSameCandles()
    {
        db.Require();
        var a = await ReaderSeed.MarketAsync(db, "CD-SLICE-A");
        var b = await ReaderSeed.MarketAsync(db, "CD-SLICE-B");
        var c = await ReaderSeed.MarketAsync(db, "CD-SLICE-C");
        foreach (var (market, id) in new[] { (a, "a"), (b, "b"), (c, "c") })
        {
            await TradeAsync(market, Ten.AddMinutes(1), id, 400_000 + market, 100);
        }

        var watched = await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [a, c], Now, TestContext.Current.CancellationToken);
        var onlyTwo = await db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM dbo.candle WHERE interval_s = 3600 AND market_id IN ({a}, {b}, {c})");
        await db.ExecuteAsync($"DELETE FROM dbo.candle WHERE market_id IN ({a}, {b}, {c})");
        var all = await Builder(pass: 1).BuildAsync(3600, Ten, Ten.AddHours(1), null, Now, TestContext.Current.CancellationToken);
        var everyone = await db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM dbo.candle WHERE interval_s = 3600 AND market_id IN ({a}, {b}, {c})");

        Assert.Equal((2, 2L), (watched, onlyTwo));
        Assert.True(all >= 3);
        Assert.Equal(3L, everyone);
        Assert.Equal(0, await Builder().BuildAsync(3600, Ten, Ten.AddHours(1), [], Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComputingWithoutStoringGivesTheCandlesAStoredBuildWouldAndLeavesNothingBehind()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-COMPUTE");
        await TradeAsync(m, Ten.AddMinutes(1), "a", 400_000, 100);
        await TradeAsync(m, Ten.AddMinutes(7), "b", 450_000, 200);
        await TickAsync(m, Ten.AddMinutes(8), 450_000, 440_000, 460_000, 9);

        var computed = await Builder().ComputeAsync(300, Ten, Ten.AddMinutes(10), [m], Now, TestContext.Current.CancellationToken);
        await Builder().BuildAsync(300, Ten, Ten.AddMinutes(10), [m], Now, TestContext.Current.CancellationToken);
        var stored = await Reader().ReadAsync(m, 300, Ten, Ten.AddMinutes(10), 10, TestContext.Current.CancellationToken);

        Assert.Equal(2, computed.Count);
        Assert.Equal(stored.Select(c => c with { }), computed.Select(c => c with { }));
        Assert.Empty(await Builder().ComputeAsync(300, Ten, Ten.AddMinutes(10), [], Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARangeOffTheBucketBoundariesAnUnstorableIntervalAndAnEmptyRangeAreRefused()
    {
        db.Require();
        var builder = Builder();

        await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildAsync(3600, Ten.AddMinutes(5), Ten.AddHours(1), null, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildAsync(3600, Ten, Ten.AddMinutes(90), null, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildAsync(3600, Ten, Ten, null, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => builder.BuildAsync(7200, Ten, Ten.AddHours(2), null, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => builder.ComputeAsync(3600, Ten.AddMinutes(5), Ten.AddHours(1), [1], Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => builder.ComputeAsync(3600, Ten, Ten.AddHours(1), null!, Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheReaderPagesByRangeReturnsTheLatestWhenNoStartIsGivenAndKnowsTheRulesTheIngestRecorded()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "CD-READ");
        for (var hour = 0; hour < 5; hour++)
        {
            await TradeAsync(m, Ten.AddHours(hour).AddMinutes(1), "r" + hour.ToString(CultureInfo.InvariantCulture), 400_000 + (hour * 1_000), 100);
        }

        await Builder().BuildAsync(3600, Ten, Ten.AddHours(5), [m], Now, TestContext.Current.CancellationToken);
        await new IngestStateStore(new DataSource(db.Options)).SetAsync(CandleReader.RulesKey, "1d=all;1h=all;1m=watched", Now, TestContext.Current.CancellationToken);
        await new IngestStateStore(new DataSource(db.Options)).SetAsync(Governor.GovernorStateKeys.OldestDay, "2026-03-01", Now, TestContext.Current.CancellationToken);

        var range = await Reader().ReadAsync(m, 3600, Ten.AddHours(1), Ten.AddHours(4), 10, TestContext.Current.CancellationToken);
        var limited = await Reader().ReadAsync(m, 3600, Ten, Ten.AddHours(5), 2, TestContext.Current.CancellationToken);
        var latest = await Reader().ReadAsync(m, 3600, null, Ten.AddHours(5), 2, TestContext.Current.CancellationToken);

        Assert.Equal([Ten.AddHours(1), Ten.AddHours(2), Ten.AddHours(3)], range.Select(c => c.BucketStart));
        Assert.Equal([Ten, Ten.AddHours(1)], limited.Select(c => c.BucketStart));
        Assert.Equal([Ten.AddHours(3), Ten.AddHours(4)], latest.Select(c => c.BucketStart));
        Assert.Equal(["1d=all", "1h=all", "1m=watched"], (await Reader().RulesAsync(TestContext.Current.CancellationToken)).Select(r => r.ToString()));
        Assert.Equal(new DateOnly(2026, 3, 1), await Reader().OldestRawDayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(m, await Reader().MarketIdAsync("CD-READ", TestContext.Current.CancellationToken));
        Assert.Null(await Reader().MarketIdAsync("CD-NOPE", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(m, 3600, null, Now, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(m, 3600, null, Now, CandleReader.MaxRows + 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnreadableRulesInTheStateTableAreNoRules()
    {
        db.Require();
        await new IngestStateStore(new DataSource(db.Options)).SetAsync(CandleReader.RulesKey, "nonsense", Now, TestContext.Current.CancellationToken);

        Assert.Empty(await Reader().RulesAsync(TestContext.Current.CancellationToken));
    }
}
