using KalTerm.Core.Money;
using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.Writing;
using KalTerm.Ingest.Backfill;
using KalTerm.Kalshi.Models;

namespace KalTerm.Ingest.Tests;

internal sealed class FakeBackfillStore : IBackfillStore
{
    public List<ClaimedGap> Pending { get; } = [];

    public HashSet<StoredTradeKey> Stored { get; } = [];

    public List<(DateTime From, DateTime Until)> StoredQueries { get; } = [];

    public List<(long Id, string Status, int Found, int Added, string Note)> Completed { get; } = [];

    public List<(long Id, int MaxAttempts, string Note)> Failures { get; } = [];

    public Exception? ClaimFails { get; set; }

    public Exception? FailRecordFails { get; set; }

    public Task<ClaimedGap?> ClaimNextAsync(DateTime now, TimeSpan settle, TimeSpan staleRunning, CancellationToken cancellationToken = default)
    {
        if (ClaimFails is not null)
        {
            return Task.FromException<ClaimedGap?>(ClaimFails);
        }

        var next = Pending.FirstOrDefault();
        if (next is not null)
        {
            Pending.Remove(next);
        }

        return Task.FromResult(next);
    }

    public Task<HashSet<StoredTradeKey>> StoredTradesAsync(DateTime from, DateTime until, CancellationToken cancellationToken = default)
    {
        StoredQueries.Add((from, until));
        return Task.FromResult(new HashSet<StoredTradeKey>(Stored));
    }

    public Task CompleteAsync(long gapId, string status, int found, int added, string note, DateTime now, CancellationToken cancellationToken = default)
    {
        Completed.Add((gapId, status, found, added, note));
        return Task.CompletedTask;
    }

    public Task FailAsync(long gapId, int maxAttempts, string note, DateTime now, CancellationToken cancellationToken = default)
    {
        if (FailRecordFails is not null)
        {
            return Task.FromException(FailRecordFails);
        }

        Failures.Add((gapId, maxAttempts, note));
        return Task.CompletedTask;
    }
}

internal sealed class FakeTradeSource : ITradeSource
{
    public List<Trade> Trades { get; } = [];

    public List<(DateTimeOffset From, DateTimeOffset Until)> Requests { get; } = [];

    public Exception? Fails { get; set; }

    public async IAsyncEnumerable<Trade> TradesAsync(DateTimeOffset from, DateTimeOffset until, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add((from, until));
        if (Fails is not null)
        {
            throw Fails;
        }

        foreach (var trade in Trades.Where(t => t.CreatedTime >= from && t.CreatedTime < until))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return trade;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal sealed class RecordingTradeWriter : IRowWriter<TradeRow>
{
    public List<TradeRow> Rows { get; } = [];

    public bool Full { get; set; }

    public bool TryWrite(TradeRow row)
    {
        if (Full)
        {
            return false;
        }

        Rows.Add(row);
        return true;
    }

    public DroppedRange? TakeDroppedRange() => null;
}

public class TradeBackfillerTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static DateTimeOffset At(DateTime moment) => new(moment, TimeSpan.Zero);

    private static Trade MakeTrade(string id, string ticker, DateTime at, string? side = "yes", bool block = false) =>
        new(id, ticker, Dollars.Parse("0.40"), Dollars.Parse("0.60"), Contracts.Parse("3.25"), At(at), side, block);

    private static ClaimedGap Gap(DateTime from, DateTime to, int attempts = 1, long id = 7) => new(id, "ingest_down", from, to, attempts);

    private sealed class Rig
    {
        public Rig(BackfillOptions? options = null)
        {
            Ids.AddRange([new KeyValuePair<string, int>("A-1", 11), new KeyValuePair<string, int>("B-2", 22)]);
            Backfiller = new TradeBackfiller(Store, Source, Writer, Ids, options ?? new BackfillOptions { PageDelay = TimeSpan.Zero });
        }

        public FakeBackfillStore Store { get; } = new();

        public FakeTradeSource Source { get; } = new();

        public RecordingTradeWriter Writer { get; } = new();

        public MarketIdMap Ids { get; } = new();

        public TradeBackfiller Backfiller { get; }
    }

    [Fact]
    public async Task WithNoGapWaitingNothingIsAskedOfKalshi()
    {
        var rig = new Rig();

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(outcome);
        Assert.Empty(rig.Source.Requests);
    }

    [Fact]
    public async Task AGapIsReadPaddedAtBothEndsAndTheNewTradesOfCatalogMarketsAreWritten()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(5)));
        rig.Source.Trades.AddRange(
        [
            MakeTrade("t-1", "A-1", Start.AddSeconds(30)),
            MakeTrade("t-2", "B-2", Start.AddMinutes(4), side: "no", block: true),
            MakeTrade("t-3", "KXMVEFOO-1", Start.AddMinutes(1)),
            MakeTrade("t-4", "NOT-A-MARKET", Start.AddMinutes(2)),
            MakeTrade("t-5", "A-1", Start.AddSeconds(-9)),   // inside the padding
            MakeTrade("t-6", "A-1", Start.AddMinutes(10)),   // outside the window
        ]);

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((At(Start.AddSeconds(-10)), At(Start.AddMinutes(5).AddSeconds(11))), Assert.Single(rig.Source.Requests));   // the end is exclusive, so one second more keeps the padded last second
        Assert.Equal(["t-1", "t-2", "t-5"], rig.Writer.Rows.Select(r => r.TradeId));
        var second = rig.Writer.Rows[1];
        Assert.Equal((Start.AddMinutes(4), 22, 400_000L, 600_000L, 325L, TakerSide.No, true), (second.Ts, second.MarketId, second.YesPriceE6, second.NoPriceE6, second.CountE2, second.TakerSide, second.IsBlock));
        Assert.Equal((7L, BackfillStatus.Done, 5, 3), (outcome!.GapId, outcome.Status, outcome.Found, outcome.Added));
        Assert.Contains("1 combo, 1 unknown market", outcome.Note, StringComparison.Ordinal);
        Assert.Equal([(7L, BackfillStatus.Done, 5, 3)], rig.Store.Completed.Select(c => (c.Id, c.Status, c.Found, c.Added)));
    }

    [Fact]
    public async Task ATradeAlreadyStoredOrReturnedTwiceIsWrittenAtMostOnce()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(2)));
        rig.Store.Stored.Add(new StoredTradeKey(11, "t-1"));
        rig.Source.Trades.AddRange(
        [
            MakeTrade("t-1", "A-1", Start.AddSeconds(5)),   // stored by the stream
            MakeTrade("t-2", "A-1", Start.AddSeconds(6)),
            MakeTrade("t-2", "A-1", Start.AddSeconds(6)),   // repeated by Kalshi
            MakeTrade("t-1", "B-2", Start.AddSeconds(7)),   // same id on another market is another trade
        ]);

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([(11, "t-2"), (22, "t-1")], rig.Writer.Rows.Select(r => (r.MarketId, r.TradeId)));
        Assert.Equal((4, 2), (outcome!.Found, outcome.Added));
        Assert.Contains("2 already stored", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningAWindowAgainWritesNothingNewOnceItsRowsAreStored()
    {
        var rig = new Rig();
        rig.Source.Trades.Add(MakeTrade("t-1", "A-1", Start.AddSeconds(5)));
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1)));
        await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);
        foreach (var row in rig.Writer.Rows)
        {
            rig.Store.Stored.Add(new StoredTradeKey(row.MarketId, row.TradeId));
        }

        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1)));
        var again = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(rig.Writer.Rows);
        Assert.Equal((1, 0), (again!.Found, again.Added));
    }

    [Fact]
    public async Task ALongWindowIsReadInContiguousSlicesAndStoredTradesAreCheckedWithATolerance()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(40)));

        await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        var from = At(Start.AddSeconds(-10));
        var to = At(Start.AddMinutes(40).AddSeconds(10));
        Assert.Equal(
            [(from, from.AddMinutes(15)), (from.AddMinutes(15), from.AddMinutes(30)), (from.AddMinutes(30), to.AddSeconds(1))],
            rig.Source.Requests);
        Assert.Equal((Start.AddSeconds(-15), Start.AddMinutes(15).AddSeconds(-5)), rig.Store.StoredQueries[0]);
        Assert.Equal((Start.AddMinutes(30).AddSeconds(-15), Start.AddMinutes(40).AddSeconds(15)), rig.Store.StoredQueries[2]);
    }

    [Fact]
    public async Task ATradeReturnedInTwoNeighbouringSlicesIsWrittenOnce()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(20)));
        var boundary = Start.AddSeconds(-10).AddMinutes(15);
        rig.Source.Trades.Add(MakeTrade("edge", "A-1", boundary.AddSeconds(-1)));
        var source = new RepeatingSource(rig.Source, MakeTrade("edge", "A-1", boundary.AddSeconds(-1)));
        var backfiller = new TradeBackfiller(rig.Store, source, rig.Writer, rig.Ids, new BackfillOptions { PageDelay = TimeSpan.Zero });

        var outcome = await backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(rig.Writer.Rows);
        Assert.Equal(2, outcome!.Found);
    }

    private sealed class RepeatingSource(FakeTradeSource inner, Trade repeated) : ITradeSource
    {
        private int _calls;

        public async IAsyncEnumerable<Trade> TradesAsync(DateTimeOffset from, DateTimeOffset until, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            await foreach (var trade in inner.TradesAsync(from, until, cancellationToken))
            {
                yield return trade;
            }

            if (_calls == 2)
            {
                yield return repeated;   // the second slice returns the last trade of the first again
            }
        }
    }

    [Fact]
    public async Task AGapLongerThanTheWindowRecoversOnlyItsMostRecentPartAndSaysSo()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddHours(10)));

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        var end = At(Start.AddHours(10).AddSeconds(10));
        Assert.Equal(end.AddHours(-6), rig.Source.Requests[0].From);
        Assert.Equal(end.AddSeconds(1), rig.Source.Requests[^1].Until);
        Assert.Equal(24, rig.Source.Requests.Count);   // six hours in quarter-hour slices
        Assert.Contains("only its most recent part was recovered", outcome!.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongRecoveryPausesAfterEveryThousandTrades()
    {
        var rig = new Rig(new BackfillOptions { PageDelay = TimeSpan.FromMilliseconds(1) });
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(10)));
        rig.Source.Trades.AddRange(Enumerable.Range(0, 2_500).Select(i => MakeTrade("t-" + i, "A-1", Start.AddSeconds(i % 500))));

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((2_500, 2_500), (outcome!.Found, outcome.Added));
    }

    [Fact]
    public async Task AFullWriterFailsTheAttemptSoTheGapIsTriedAgain()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1), attempts: 1));
        rig.Source.Trades.Add(MakeTrade("t-1", "A-1", Start.AddSeconds(5)));
        rig.Writer.Full = true;

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillStatus.Pending, outcome!.Status);
        Assert.Contains("writer is full", Assert.Single(rig.Store.Failures).Note, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Completed);
    }

    [Fact]
    public async Task TheThirdFailedAttemptEndsInFailed()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1), attempts: 3));
        rig.Source.Fails = new HttpRequestException("kalshi is down");

        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((BackfillStatus.Failed, "kalshi is down"), (outcome!.Status, outcome.Note));
        Assert.Equal((7L, 3, "kalshi is down"), Assert.Single(rig.Store.Failures));
    }

    [Fact]
    public async Task AnUnreadableGapLogOrAFailureThatCannotBeRecordedNeverEscapes()
    {
        var rig = new Rig();
        rig.Store.ClaimFails = new InvalidOperationException("database down");
        Assert.Null(await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken));

        rig.Store.ClaimFails = null;
        rig.Store.FailRecordFails = new InvalidOperationException("still down");
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1)));
        rig.Source.Fails = new HttpRequestException("kalshi is down");
        var outcome = await rig.Backfiller.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillStatus.Pending, outcome!.Status);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var rig = new Rig();
        rig.Store.Pending.Add(Gap(Start, Start.AddMinutes(1)));
        rig.Source.Trades.Add(MakeTrade("t-1", "A-1", Start.AddSeconds(5)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Backfiller.RunOnceAsync(cts.Token));
        Assert.Empty(rig.Store.Failures);
    }

    [Fact]
    public void OptionsMustBeUsableAndPartsPresent()
    {
        new BackfillOptions().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(new BackfillOptions { MaxWindow = TimeSpan.FromSeconds(30) }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new BackfillOptions { Slice = TimeSpan.Zero }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new BackfillOptions { Padding = TimeSpan.FromSeconds(-1) }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new BackfillOptions { MaxAttempts = 0 }.Validate);
        Assert.Throws<ArgumentNullException>(() => new TradeBackfiller(null!, new FakeTradeSource(), new RecordingTradeWriter(), new MarketIdMap()));
        Assert.Throws<ArgumentNullException>(() => new TradeBackfiller(new FakeBackfillStore(), null!, new RecordingTradeWriter(), new MarketIdMap()));
        Assert.Throws<ArgumentNullException>(() => new TradeBackfiller(new FakeBackfillStore(), new FakeTradeSource(), null!, new MarketIdMap()));
        Assert.Throws<ArgumentNullException>(() => new TradeBackfiller(new FakeBackfillStore(), new FakeTradeSource(), new RecordingTradeWriter(), null!));
    }

    [Fact]
    public void ARestTradeMapsOntoItsStoredRowWithAnUnknownTakerSideStoredAsZero()
    {
        var row = StreamMapper.ToRow(MakeTrade("t-9", "A-1", Start.AddSeconds(1), side: null), 5);

        Assert.Equal((Start.AddSeconds(1), 5, "t-9", TakerSide.Unknown, false), (row.Ts, row.MarketId, row.TradeId, row.TakerSide, row.IsBlock));
        Assert.Throws<ArgumentNullException>(() => StreamMapper.ToRow((Trade)null!, 1));
    }
}
