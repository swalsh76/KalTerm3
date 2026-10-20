using KalTerm.Data;
using KalTerm.Data.Metrics;

namespace KalTerm.Data.Tests;

public class MetricsCalculatorTests
{
    private static MetricsInputs Inputs(long? last = 560_000, long? reference = 540_000, long? bid = 550_000, long? ask = 570_000, decimal volume = 400, decimal priceTimesCount = 220_000_000, IReadOnlyList<long>? closes = null, bool partial = false) =>
        new(new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 10, 23, 59, 0, DateTimeKind.Utc), last, bid, ask, 9_900, 7, volume, priceTimesCount, reference, closes ?? [], partial);

    [Fact]
    public void TheVwapIsTheSumOfPriceTimesCountOverTheSumOfCounts()
    {
        // 1 contract at 0.40 and 3 at 0.60: (400000 * 100 + 600000 * 300) / 400 = 550000
        Assert.Equal(550_000L, MetricsCalculator.Vwap(220_000_000m, 400m));
    }

    [Theory]
    [InlineData(3, 2, 2)]    // 1.5 rounds up
    [InlineData(5, 3, 2)]    // 1.67
    [InlineData(4, 3, 1)]    // 1.33
    [InlineData(0, 5, 0)]
    public void TheVwapRoundsHalfUp(int priceTimesCount, int volume, long expected)
    {
        Assert.Equal(expected, MetricsCalculator.Vwap(priceTimesCount, volume));
    }

    [Fact]
    public void WithoutVolumeThereIsNoVwapAndAHugeVolumeDoesNotOverflow()
    {
        Assert.Null(MetricsCalculator.Vwap(0m, 0m));
        Assert.Equal(500_000L, MetricsCalculator.Vwap(500_000m * 90_000_000_000_000_000m, 90_000_000_000_000_000m));
    }

    [Fact]
    public void AnAverageTakesTheNewestClosesAndNeedsAllOfThem()
    {
        long[] closes = [30, 20, 10, 1_000];

        Assert.Equal(20L, MetricsCalculator.Sma(closes, 3));
        Assert.Null(MetricsCalculator.Sma(closes, 5));
        Assert.Equal(2L, MetricsCalculator.Sma([1, 2], 2));   // 1.5 rounds up
    }

    [Fact]
    public void TheVolatilityIsTheSampleDeviationOfTheChangesBetweenCloses()
    {
        // changes +100000 and -50000: variance = 1.125e10, deviation 106066.017
        Assert.Equal(106_066L, MetricsCalculator.Volatility([500_000, 400_000, 450_000]));
        Assert.Equal(0L, MetricsCalculator.Volatility([3, 2, 1]));
    }

    [Fact]
    public void TheVolatilityRoundsHalfUp()
    {
        // changes +2 and -2: variance 8, deviation 2.83 rounds to 3
        Assert.Equal(3L, MetricsCalculator.Volatility([12, 10, 12]));
        // changes +1 and -1: variance 2, deviation 1.41 rounds to 1
        Assert.Equal(1L, MetricsCalculator.Volatility([11, 10, 11]));
    }

    [Fact]
    public void TheVolatilityNeedsThreeClosesAndLooksOnlyAtTheNewestTwentyFour()
    {
        var wild = Enumerable.Range(0, 30).Select(i => i < 24 ? 500_000L + (i % 2 * 1_000) : (i * 99_991L)).ToList();

        Assert.Null(MetricsCalculator.Volatility([1, 2]));
        Assert.Equal(MetricsCalculator.Volatility([.. wild.Take(24)]), MetricsCalculator.Volatility(wild));
        Assert.Equal(MetricsCalculator.Volatility([.. wild.Take(24)]), MetricsCalculator.Volatility([.. wild.Take(24), 1, 2, 3]));
    }

    [Fact]
    public void ComputeCombinesTheParts()
    {
        var metrics = MetricsCalculator.Compute(Inputs(closes: [.. Enumerable.Repeat(500_000L, 50)], partial: true));

        Assert.Equal(20_000L, metrics.Change24hE6);
        Assert.Equal(20_000L, metrics.SpreadE6);
        Assert.Equal(550_000L, metrics.Vwap24hE6);
        Assert.Equal((500_000L, 500_000L, 0L), (metrics.Sma20E6, metrics.Sma50E6, metrics.Volatility24hE6));
        Assert.Equal((400L, 7L, 9_900L, true), (metrics.VolumeE2, metrics.TradeCount, metrics.OpenInterestE2, metrics.Partial));
    }

    [Fact]
    public void WhatIsMissingStaysMissingAndIsNeverMadeUp()
    {
        var metrics = MetricsCalculator.Compute(Inputs(last: null, reference: null, bid: 1, ask: null, volume: 0, priceTimesCount: 0));

        Assert.Null(metrics.Change24hE6);
        Assert.Null(metrics.SpreadE6);
        Assert.Null(metrics.Vwap24hE6);
        Assert.Null(metrics.Sma20E6);
        Assert.Null(metrics.Volatility24hE6);
        Assert.Null(MetricsCalculator.Compute(Inputs(reference: null)).Change24hE6);
    }
}

public class MetricsReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private MetricsReader Reader() => new(new DataSource(db.Options));

    private Task TradeAsync(int market, DateTime at, string id, long price, long count) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES ('{ReaderSeed.Time(at)}', {market}, '{id}', {price}, {1_000_000 - price}, {count}, 1, 0)"));

    private Task TickAsync(int market, DateTime at, long price, long bid, long ask, long interest) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, open_interest_e2, volume_e2) VALUES ('{ReaderSeed.Time(at)}', {market}, {price}, {bid}, {ask}, {interest}, 1)"));

    private Task HourAsync(int market, DateTime start, long? close, bool partial = false)
    {
        var price = close is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        return db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.candle (interval_s, market_id, bucket_start, open_e6, high_e6, low_e6, close_e6, volume_e2, trade_count, trades_partial, book_partial, built_at) VALUES (3600, {market}, '{ReaderSeed.Time(start)}', {price}, {price}, {price}, {price}, 100, 1, {(partial ? 1 : 0)}, 0, '{ReaderSeed.Time(Now)}')"));
    }

    [Fact]
    public async Task AnUnknownMarketHasNoInputs()
    {
        db.Require();

        Assert.Null(await Reader().ReadAsync("MT-NOPE", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyTradesInTheLast24HoursCountAndTheLatestTickGivesTheBook()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "MT-WINDOW");
        await TradeAsync(m, Now.AddHours(-24), "edge", 999_000, 5_000);   // exactly 24 h ago: outside (the window is open at its start)
        await TradeAsync(m, Now.AddHours(-23), "a", 400_000, 100);
        await TradeAsync(m, Now.AddHours(-1), "b", 600_000, 300);
        await TradeAsync(m, Now.AddMinutes(1), "future", 1, 1);
        await TickAsync(m, Now.AddHours(-2), 410_000, 400_000, 420_000, 1_000);
        await TickAsync(m, Now.AddMinutes(-5), 590_000, 580_000, 600_000, 2_000);
        await TickAsync(m, Now.AddMinutes(5), 1, 1, 1, 1);   // after as-of

        var inputs = (await Reader().ReadAsync("MT-WINDOW", Now, TestContext.Current.CancellationToken))!;

        Assert.Equal((2L, 400m, 220_000_000m), (inputs.TradeCount, inputs.VolumeE2, inputs.PriceTimesCount));
        Assert.Equal((590_000L, 580_000L, 600_000L, 2_000L), (inputs.LastE6, inputs.YesBidE6, inputs.YesAskE6, inputs.OpenInterestE2));
        Assert.Equal((Now.AddMinutes(-5), DateTimeKind.Utc), (inputs.LastTime, inputs.LastTime!.Value.Kind));
        Assert.False(inputs.Partial);
        Assert.Empty(inputs.Closes);
        Assert.Null(inputs.ReferenceCloseE6);
    }

    [Fact]
    public async Task ACandleStoredBeforeNowMinus24HoursIsTheReferenceAndPricedClosesComeNewestFirst()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "MT-HOURLY");
        var hour = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc);
        await HourAsync(m, hour.AddHours(-30), 100_000);
        await HourAsync(m, hour.AddHours(-25), 200_000);   // ends at 12:00 the day before: before the 12:30 day-before reference, so it is the reference
        await HourAsync(m, hour.AddHours(-24), 300_000);   // ends 13:00: after the reference moment
        await HourAsync(m, hour.AddHours(-3), null);        // no trades: no price, not a close
        await HourAsync(m, hour.AddHours(-2), 500_000);
        await HourAsync(m, hour.AddHours(-1), 510_000);

        var inputs = (await Reader().ReadAsync("MT-HOURLY", Now, TestContext.Current.CancellationToken))!;

        Assert.Equal(200_000L, inputs.ReferenceCloseE6);
        Assert.Equal([510_000L, 500_000L, 300_000L, 200_000L, 100_000L], inputs.Closes);
    }

    [Fact]
    public async Task OnlyTheLatestFiftyClosesAreKeptAndAPartialCandleAmongThemMarksTheMetricsPartial()
    {
        db.Require();
        var inside = await ReaderSeed.MarketAsync(db, "MT-PARTIAL-IN");
        var outside = await ReaderSeed.MarketAsync(db, "MT-PARTIAL-OUT");
        var hour = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 60; i++)
        {
            await HourAsync(inside, hour.AddHours(-i), 500_000 + i, partial: i == 10);
            await HourAsync(outside, hour.AddHours(-i), 500_000 + i, partial: i == 55);   // older than the 50 closes used
        }

        var mine = (await Reader().ReadAsync("MT-PARTIAL-IN", Now, TestContext.Current.CancellationToken))!;
        var far = (await Reader().ReadAsync("MT-PARTIAL-OUT", Now, TestContext.Current.CancellationToken))!;

        Assert.Equal(50, mine.Closes.Count);
        Assert.Equal(500_001L, mine.Closes[0]);
        Assert.True(mine.Partial);
        Assert.False(far.Partial);
    }

    [Fact]
    public async Task AGapOfTheMarketOverlappingTheWindowMarksItPartialButOneThatHadNotHappenedYetDoesNot()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "MT-GAPPED");
        await db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.ingest_gap (kind, market_id, started_at, ended_at) VALUES ('ws_lost', {m}, '{ReaderSeed.Time(Now.AddHours(-3))}', '{ReaderSeed.Time(Now.AddHours(-2))}')"));

        var now = (await Reader().ReadAsync("MT-GAPPED", Now, TestContext.Current.CancellationToken))!;
        var before = (await Reader().ReadAsync("MT-GAPPED", Now.AddHours(-30), TestContext.Current.CancellationToken))!;

        Assert.True(now.Partial);
        Assert.False(before.Partial);
    }
}
