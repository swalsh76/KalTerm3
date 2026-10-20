using KalTerm.Data;
using KalTerm.Data.Metrics;

namespace KalTerm.Data.Tests;

public class MetricsInputsBuilderTests
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);
    private static readonly MetricsTradeSums NoTrades = new(0, 0m, 0m);

    private static HourlyClose Hour(int hoursAgo, long? close, bool partial = false) => new(new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc).AddHours(-hoursAgo), close, partial);

    [Fact]
    public void TheReferenceIsTheNewestPricedCandleThatEndedBy24HoursAgoAndClosesRunNewestFirst()
    {
        // 25 hours ago ends 12:00 yesterday (before 12:30): the reference. 24 hours ago ends 13:00: too late. An unpriced candle is skipped.
        var inputs = MetricsInputsBuilder.Build(Now, null, NoTrades, false, [Hour(26, 100), Hour(25, 200), Hour(24, 300), Hour(3, null), Hour(2, 500)]);

        Assert.Equal(200L, inputs.ReferenceCloseE6);
        Assert.Equal([500L, 300L, 200L, 100L], inputs.Closes);
    }

    [Fact]
    public void OnlyTheNewestFiftyClosesCountAndOnlyTheirPartialFlagsMatter()
    {
        var candles = Enumerable.Range(1, 60).Select(i => Hour(i, 1_000 + i, partial: i == 55)).ToList();

        var inputs = MetricsInputsBuilder.Build(Now, null, NoTrades, false, candles);

        Assert.Equal(50, inputs.Closes.Count);
        Assert.False(inputs.Partial);
        Assert.True(MetricsInputsBuilder.Build(Now, null, NoTrades, false, [Hour(5, 1, partial: true)]).Partial);
        Assert.True(MetricsInputsBuilder.Build(Now, null, NoTrades, true, []).Partial);
    }

    [Fact]
    public void ATickIsCarriedOverAndMarkedUtc()
    {
        var tick = new MetricsTick(DateTime.SpecifyKind(Now.AddMinutes(-3), DateTimeKind.Unspecified), 5, 4, 6, 7);

        var inputs = MetricsInputsBuilder.Build(Now, tick, new MetricsTradeSums(2, 300m, 9m), false, []);

        Assert.Equal((Now.AddMinutes(-3), DateTimeKind.Utc), (inputs.LastTime, inputs.LastTime!.Value.Kind));
        Assert.Equal((5L, 4L, 6L, 7L, 2L, 300m, 9m), (inputs.LastE6, inputs.YesBidE6, inputs.YesAskE6, inputs.OpenInterestE2, inputs.TradeCount, inputs.VolumeE2, inputs.PriceTimesCount));
    }
}

public class MetricsBatchReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Hour12 = new(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc);

    private Task TradeAsync(int market, DateTime at, string id, long price, long count) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES ('{ReaderSeed.Time(at)}', {market}, '{id}', {price}, {1_000_000 - price}, {count}, 1, 0)"));

    private Task TickAsync(int market, DateTime at, long price, long bid, long ask, long interest) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, open_interest_e2, volume_e2) VALUES ('{ReaderSeed.Time(at)}', {market}, {price}, {bid}, {ask}, {interest}, 1)"));

    private Task HourAsync(int market, DateTime start, long close, bool partial = false) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.candle (interval_s, market_id, bucket_start, open_e6, high_e6, low_e6, close_e6, volume_e2, trade_count, trades_partial, book_partial, built_at) VALUES (3600, {market}, '{ReaderSeed.Time(start)}', {close}, {close}, {close}, {close}, 100, 1, {(partial ? 1 : 0)}, 0, '{ReaderSeed.Time(Now)}')"));

    private async Task<Dictionary<int, MetricsInputs>> ReadAsync(int idsPerBatch = MetricsBatchReader.IdsPerBatch)
    {
        var found = new Dictionary<int, MetricsInputs>();
        await new MetricsBatchReader(new DataSource(db.Options), idsPerBatch).ReadAsync(
            Now,
            batch =>
            {
                foreach (var market in batch)
                {
                    found[market.MarketId] = market.Inputs;
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        return found;
    }

    [Fact]
    public async Task ABusyMarketGetsExactlyTheInputsTheSingleMarketReaderGives()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "MB-BUSY");
        await TradeAsync(m, Now.AddHours(-23), "a", 400_000, 100);
        await TradeAsync(m, Now.AddHours(-2), "b", 600_000, 300);
        await TickAsync(m, Now.AddHours(-3), 111, 1, 2, 3);    // older than the tick window: the batch never reads it
        await TickAsync(m, Now.AddMinutes(-20), 590_000, 580_000, 600_000, 2_000);
        await TickAsync(m, Now.AddMinutes(-10), 595_000, 585_000, 605_000, 2_100);
        for (var i = 1; i <= 30; i++)
        {
            await HourAsync(m, Hour12.AddHours(-i), 500_000 + (i * 1_000), partial: i == 4);
        }

        var batch = (await ReadAsync())[m];
        var single = (await new MetricsReader(new DataSource(db.Options)).ReadAsync("MB-BUSY", Now, TestContext.Current.CancellationToken))!;

        Assert.Equal(single.Closes, batch.Closes);
        Assert.Equal(single with { Closes = [] }, batch with { Closes = [] });
        Assert.Equal(595_000L, batch.LastE6);
        Assert.True(batch.Partial);
    }

    [Fact]
    public async Task OnlyMarketsWithRecentActivityAreFoundAndOldTicksNeverCount()
    {
        db.Require();
        var traded = await ReaderSeed.MarketAsync(db, "MB-TRADED");
        var ticked = await ReaderSeed.MarketAsync(db, "MB-TICKED");
        var hourly = await ReaderSeed.MarketAsync(db, "MB-HOURLY");
        var idle = await ReaderSeed.MarketAsync(db, "MB-IDLE");
        var staleTick = await ReaderSeed.MarketAsync(db, "MB-STALETICK");
        await TradeAsync(traded, Now.AddHours(-5), "t", 300_000, 200);
        await TickAsync(ticked, Now.AddMinutes(-30), 400_000, 390_000, 410_000, 5);
        await HourAsync(hourly, Hour12.AddHours(-3), 450_000);
        await HourAsync(idle, Hour12.AddHours(-30), 450_000);   // older than 25 hours
        await TradeAsync(idle, Now.AddHours(-25), "old", 1, 1);   // outside the window
        await TickAsync(staleTick, Now.AddHours(-2), 400_000, 390_000, 410_000, 5);   // older than the tick window

        var found = await ReadAsync();

        Assert.Contains(traded, found.Keys);
        Assert.Contains(ticked, found.Keys);
        Assert.Contains(hourly, found.Keys);
        Assert.DoesNotContain(idle, found.Keys);
        Assert.DoesNotContain(staleTick, found.Keys);
        Assert.Equal((null, 1L, 200m), (found[traded].LastE6, found[traded].TradeCount, found[traded].VolumeE2));   // no tick in the last hour: no book figures
        Assert.Equal((400_000L, 0L), (found[ticked].LastE6, found[ticked].TradeCount));
    }

    [Fact]
    public async Task SlicingTheIdsGivesTheSameAnswerAsOnePass()
    {
        db.Require();
        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var m = await ReaderSeed.MarketAsync(db, "MB-SLICE-" + i);
            ids.Add(m);
            await TradeAsync(m, Now.AddHours(-1), "s" + i, 100_000 + i, 100);
        }

        var whole = await ReadAsync();
        var sliced = await ReadAsync(idsPerBatch: 2);

        Assert.Equal(whole.Keys.Order(), sliced.Keys.Order());
        foreach (var id in ids)
        {
            Assert.Equal(whole[id].PriceTimesCount, sliced[id].PriceTimesCount);
        }
    }
}

public class MarketMetricsStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private MarketMetricsStore Store() => new(new DataSource(db.Options));

    private static MarketMetrics Metrics(long? last = 500_000, long volume = 100, bool partial = false) =>
        new(Now, Now.AddMinutes(-1), last, 1_000, volume, 3, 77, 490_000, 510_000, 20_000, 501_000, null, null, null, partial);

    [Fact]
    public async Task NewMarketsAreInsertedUnchangedOnesAreLeftAloneAndChangedOnesAreUpdated()
    {
        db.Require();
        var a = await ReaderSeed.MarketAsync(db, "MS-A");
        var b = await ReaderSeed.MarketAsync(db, "MS-B");
        var store = Store();

        var first = await store.UpsertAsync([(a, Metrics()), (b, Metrics(volume: 5))], Now, TestContext.Current.CancellationToken);
        var same = await store.UpsertAsync([(a, Metrics()), (b, Metrics(volume: 5))], Now.AddMinutes(5), TestContext.Current.CancellationToken);
        var changed = await store.UpsertAsync([(a, Metrics(last: null)), (b, Metrics(volume: 5))], Now.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.Equal((2, 0, 1), (first, same, changed));
        Assert.Equal(Now, await db.ScalarAsync<DateTime>($"SELECT changed_at FROM dbo.market_metrics WHERE market_id = {b}"));   // untouched since the first write
        Assert.Equal(Now.AddMinutes(10), await db.ScalarAsync<DateTime>($"SELECT changed_at FROM dbo.market_metrics WHERE market_id = {a}"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.market_metrics WHERE market_id = {a} AND last_e6 IS NOT NULL"));
        Assert.Equal(5L, await db.ScalarAsync<long>($"SELECT volume_e2 FROM dbo.market_metrics WHERE market_id = {b}"));
        Assert.Equal((77L, 490_000L, 20_000L, 501_000L), (
            await db.ScalarAsync<long>($"SELECT open_interest_e2 FROM dbo.market_metrics WHERE market_id = {b}"),
            await db.ScalarAsync<long>($"SELECT yes_bid_e6 FROM dbo.market_metrics WHERE market_id = {b}"),
            await db.ScalarAsync<long>($"SELECT spread_e6 FROM dbo.market_metrics WHERE market_id = {b}"),
            await db.ScalarAsync<long>($"SELECT vwap_e6 FROM dbo.market_metrics WHERE market_id = {b}")));
    }

    [Fact]
    public async Task MarketsOutsideTheKeptSetAreDeleted()
    {
        db.Require();
        var keep = await ReaderSeed.MarketAsync(db, "MS-KEEP");
        var drop = await ReaderSeed.MarketAsync(db, "MS-DROP");
        var store = Store();
        await store.UpsertAsync([(keep, Metrics()), (drop, Metrics())], Now, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteOthersAsync([keep], TestContext.Current.CancellationToken);

        Assert.True(deleted >= 1);
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.market_metrics WHERE market_id = {keep}"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.market_metrics WHERE market_id = {drop}"));
        Assert.Equal(0, await store.DeleteOthersAsync([keep], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyBatchWritesNothing()
    {
        db.Require();

        Assert.Equal(0, await Store().UpsertAsync([], Now, TestContext.Current.CancellationToken));
    }
}
