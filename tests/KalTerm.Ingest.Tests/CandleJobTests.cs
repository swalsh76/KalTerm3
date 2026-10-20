using KalTerm.Data.Candles;
using KalTerm.Data.Markets;
using KalTerm.Ingest.Candles;

namespace KalTerm.Ingest.Tests;

internal sealed class FakeCandleBuilder : ICandleBuilder
{
    public List<(int Interval, DateTime From, DateTime Until, int[]? Markets)> Builds { get; } = [];

    public Exception? Fails { get; set; }

    public int Written { get; set; } = 3;

    public Task<int> BuildAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int>? marketIds, DateTime now, CancellationToken cancellationToken = default)
    {
        if (Fails is not null)
        {
            return Task.FromException<int>(Fails);
        }

        Builds.Add((intervalSeconds, from, until, marketIds?.ToArray()));
        return Task.FromResult(Written);
    }
}

internal sealed class FakeCandleStore : ICandleJobStore
{
    public Dictionary<int, DateTime> Through { get; } = [];

    public DateTime? RawFrom { get; set; }

    public List<GapToRebuild> Gaps { get; } = [];

    public List<long> Rebuilt { get; } = [];

    public int RulesSaved { get; private set; }

    public Task SaveRulesAsync(IReadOnlyList<CandleRule> rules, DateTime now, CancellationToken cancellationToken = default)
    {
        RulesSaved++;
        return Task.CompletedTask;
    }

    public Task<DateTime?> GetThroughAsync(int intervalSeconds, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(Through.TryGetValue(intervalSeconds, out var value) ? value : null);

    public Task SetThroughAsync(int intervalSeconds, DateTime through, DateTime now, CancellationToken cancellationToken = default)
    {
        Through[intervalSeconds] = through;
        return Task.CompletedTask;
    }

    public Task<DateTime?> RawFromAsync(CancellationToken cancellationToken = default) => Task.FromResult(RawFrom);

    public Task<IReadOnlyList<GapToRebuild>> GapsToRebuildAsync(DateTime endedBefore, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GapToRebuild>>([.. Gaps.Where(g => g.EndedAt < endedBefore)]);

    public Task MarkRebuiltAsync(long gapId, DateTime now, CancellationToken cancellationToken = default)
    {
        Rebuilt.Add(gapId);
        Gaps.RemoveAll(g => g.Id == gapId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCandleJob : ICandleJob
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Exception? Failure { get; set; }

    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Failure is null ? Task.FromResult(0) : Task.FromException<int>(Failure);
    }
}

public class CandleJobTests
{
    private static readonly DateTime Midnight = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static (CandleJob Job, FakeCandleBuilder Builder, FakeCandleStore Store, Clock Clock) Rig(string rules, DateTime now, DateTime? rawFrom = null, int[]? watched = null, CandleJobOptions? options = null)
    {
        var builder = new FakeCandleBuilder();
        var store = new FakeCandleStore { RawFrom = rawFrom ?? Midnight };
        var clock = new Clock(now);
        return (new CandleJob(builder, store, CandleRules.Parse(rules), () => watched ?? [], options, clock), builder, store, clock);
    }

    [Fact]
    public async Task TheFirstRunBuildsFromTheOldestRawDataUpToTheLastClosedBucketAndRemembersWhereItGot()
    {
        var (job, builder, store, _) = Rig("1h=all", Midnight.AddHours(13).AddMinutes(20));

        var written = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, written);
        Assert.Equal([(3600, Midnight, Midnight.AddHours(13), (int[]?)null)], builder.Builds);
        Assert.Equal(Midnight.AddHours(13), store.Through[3600]);
        Assert.Equal(1, store.RulesSaved);
    }

    [Fact]
    public async Task ABucketIsBuiltOnlyAfterItsSettlingTimeAndNothingIsBuiltTwice()
    {
        var (job, builder, store, clock) = Rig("1h=all", Midnight.AddHours(13).AddSeconds(30));
        store.Through[3600] = Midnight.AddHours(12);

        var early = await job.RunOnceAsync(TestContext.Current.CancellationToken);   // 13:00:30: the 12:00 bucket closed but 13:00 only 30 seconds ago
        clock.Now = Midnight.AddHours(13).AddSeconds(100);
        var later = await job.RunOnceAsync(TestContext.Current.CancellationToken);
        var again = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0, 3, 0), (early, later, again));
        Assert.Equal([(3600, Midnight.AddHours(12), Midnight.AddHours(13), (int[]?)null)], builder.Builds);
        Assert.Equal(1, store.RulesSaved);
    }

    [Fact]
    public async Task ALongCatchUpIsTakenInSpansSoItNeverHoldsTheDatabaseForLong()
    {
        var (job, builder, store, _) = Rig("1m=all;1d=all", Midnight.AddDays(3).AddHours(1), rawFrom: Midnight);

        await job.RunOnceAsync(TestContext.Current.CancellationToken);
        await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [(86400, Midnight, Midnight.AddDays(1)), (60, Midnight, Midnight.AddHours(6)), (86400, Midnight.AddDays(1), Midnight.AddDays(2)), (60, Midnight.AddHours(6), Midnight.AddHours(12))],
            builder.Builds.Select(b => (b.Interval, b.From, b.Until)));
        Assert.Equal(Midnight.AddDays(2), store.Through[86400]);
    }

    [Fact]
    public async Task NothingBeforeTheOldestRawDayIsEverBuiltBecauseRebuildingFromRowsThatAreGoneWouldEraseTheCandle()
    {
        var (job, builder, store, _) = Rig("1h=all", Midnight.AddDays(40).AddHours(5), rawFrom: Midnight.AddDays(35));
        store.Through[3600] = Midnight.AddDays(2);   // far behind: the retention dropped those days

        await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Midnight.AddDays(35), builder.Builds[0].From);
    }

    [Fact]
    public async Task WithoutKnowingTheOldestRawDayAFreshJobWaitsInsteadOfGuessing()
    {
        var builder = new FakeCandleBuilder();
        var store = new FakeCandleStore { RawFrom = null };
        var job = new CandleJob(builder, store, CandleRules.Parse("1h=all"), () => [], null, new Clock(Midnight.AddHours(5)));

        var written = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Empty(builder.Builds);
        Assert.Empty(store.Through);
    }

    [Fact]
    public async Task AWatchedIntervalIsBuiltForTheWatchListOnlyAndSkippedWhenThereIsNoneButStillAdvances()
    {
        var (job, builder, store, _) = Rig("1m=watched", Midnight.AddHours(2), watched: [4, 9]);
        await job.RunOnceAsync(TestContext.Current.CancellationToken);
        var (empty, emptyBuilder, emptyStore, _) = Rig("1m=watched", Midnight.AddHours(2), watched: []);

        var written = await empty.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([4, 9], builder.Builds[0].Markets!);
        Assert.Equal(0, written);
        Assert.Empty(emptyBuilder.Builds);
        Assert.Equal(Midnight.AddHours(1).AddMinutes(58), store.Through[60]);
        Assert.Equal(Midnight.AddHours(1).AddMinutes(58), emptyStore.Through[60]);
    }

    [Fact]
    public async Task WhenATradeBackfillFinishesTheCandlesOverItsGapAreRebuiltAndOnlyThose()
    {
        var (job, builder, store, _) = Rig("1d=all;1h=all", Midnight.AddHours(14));
        store.Through[86400] = Midnight;
        store.Through[3600] = Midnight.AddHours(13);
        store.Gaps.Add(new GapToRebuild(5, Midnight.AddHours(10).AddMinutes(10), Midnight.AddHours(11).AddMinutes(10)));

        await job.RunOnceAsync(TestContext.Current.CancellationToken);

        var rebuilds = builder.Builds.Where(b => b.Until <= Midnight.AddHours(13) && b.From >= Midnight.AddHours(10)).ToList();
        Assert.Equal([(3600, Midnight.AddHours(10), Midnight.AddHours(12))], rebuilds.Select(b => (b.Interval, b.From, b.Until)));   // the day bucket is not built yet, the hours around the gap are
        Assert.Equal([5L], store.Rebuilt);
    }

    [Fact]
    public async Task AGapOlderThanTheRawDataOrNewerThanWhatIsBuiltIsMarkedDoneWithoutRebuildingWhatCannotBe()
    {
        var (job, builder, store, _) = Rig("1h=all", Midnight.AddDays(40).AddHours(14), rawFrom: Midnight.AddDays(35));
        store.Through[3600] = Midnight.AddDays(40).AddHours(13);
        store.Gaps.Add(new GapToRebuild(1, Midnight.AddDays(2), Midnight.AddDays(2).AddMinutes(5)));   // its raw rows are gone
        store.Gaps.Add(new GapToRebuild(2, Midnight.AddDays(40).AddHours(13).AddMinutes(20), Midnight.AddDays(40).AddHours(13).AddMinutes(30)));   // its bucket is not built yet: the normal build will see it

        await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], store.Rebuilt);
        Assert.DoesNotContain(builder.Builds, b => b.From < Midnight.AddDays(35) || b.From >= Midnight.AddDays(40).AddHours(13));
    }

    [Fact]
    public async Task AFailureIsLoggedNotThrownAndTheProgressStaysSoTheSameBucketsAreTriedAgain()
    {
        var (job, builder, store, _) = Rig("1h=all", Midnight.AddHours(5));
        builder.Fails = new InvalidOperationException("deadlock victim");

        var written = await job.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.Through);
        builder.Fails = null;
        var retried = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0, 3), (written, retried));
        Assert.Equal(Midnight.AddHours(4), store.Through[3600]);   // 05:00 less the settling time, floored to the hour
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var (job, builder, _, _) = Rig("1h=all", Midnight.AddHours(5));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        builder.Fails = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.RunOnceAsync(cts.Token));
    }

    [Fact]
    public void WatchListTickersBecomeIdsAndUnknownOnesAreLeftOut()
    {
        var ids = new MarketIdMap();
        ids.AddRange([new KeyValuePair<string, int>("A-1", 11), new KeyValuePair<string, int>("B-2", 22)]);

        var watched = CandleJob.WatchedIds(["A-1", "NOPE", "B-2"], ids);

        Assert.Equal([11, 22], watched());
        Assert.Throws<ArgumentNullException>(() => CandleJob.WatchedIds(null!, ids));
        Assert.Throws<ArgumentNullException>(() => CandleJob.WatchedIds([], null!));
    }

    [Fact]
    public void OptionsAndPartsAreChecked()
    {
        new CandleJobOptions().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(new CandleJobOptions { Settle = TimeSpan.FromSeconds(-1) }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new CandleJobOptions { LongSpan = TimeSpan.FromMinutes(5) }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new CandleJobOptions { ShortSpan = TimeSpan.Zero }.Validate);
        var rules = CandleRules.Parse("1h=all");
        Assert.Throws<ArgumentNullException>(() => new CandleJob(null!, new FakeCandleStore(), rules, () => []));
        Assert.Throws<ArgumentNullException>(() => new CandleJob(new FakeCandleBuilder(), null!, rules, () => []));
        Assert.Throws<ArgumentNullException>(() => new CandleJob(new FakeCandleBuilder(), new FakeCandleStore(), null!, () => []));
        Assert.Throws<ArgumentNullException>(() => new CandleJob(new FakeCandleBuilder(), new FakeCandleStore(), rules, null!));
    }
}
