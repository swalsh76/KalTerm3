using KalTerm.Data.Governor;
using KalTerm.Data.State;

namespace KalTerm.Data.Tests;

public class StorageBudgetTests
{
    [Theory]
    [InlineData(0L, 100L, 0)]
    [InlineData(79L, 100L, 79)]
    [InlineData(80L, 100L, 80)]
    [InlineData(89L, 100L, 89)]
    [InlineData(90L, 100L, 90)]
    [InlineData(150L, 100L, 150)]
    public void PercentIsWholePercentOfTheCapRoundedDown(long used, long max, int percent)
    {
        Assert.Equal(percent, StorageBudget.Percent(used, max));
    }

    [Theory]
    [InlineData(79L, BudgetLevel.Ok)]
    [InlineData(80L, BudgetLevel.Warn)]
    [InlineData(89L, BudgetLevel.Warn)]
    [InlineData(90L, BudgetLevel.Shed)]
    [InlineData(120L, BudgetLevel.Shed)]
    public void LevelsStartAtEightyAndNinetyPercent(long used, BudgetLevel level)
    {
        Assert.Equal(level, StorageBudget.Level(used, 100));
    }

    [Fact]
    public void NoCapMeansNoPercentAndAlwaysOk()
    {
        Assert.Null(StorageBudget.Percent(500, null));
        Assert.Null(StorageBudget.Percent(500, 0));
        Assert.Equal(BudgetLevel.Ok, StorageBudget.Level(long.MaxValue, null));
    }

    [Fact]
    public void ARealisticFiveHundredGigabyteCapDoesNotOverflow()
    {
        const long Gb = 1024L * 1024 * 1024;

        Assert.Equal(BudgetLevel.Shed, StorageBudget.Level(451 * Gb, 500 * Gb));
        Assert.Equal(BudgetLevel.Warn, StorageBudget.Level(401 * Gb, 500 * Gb));
        Assert.Equal(int.MaxValue, StorageBudget.Percent(long.MaxValue / 100, 1));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(30, true)]
    public void RetentionMustExceedTheDaysThatAreAlwaysKept(int days, bool valid)
    {
        var options = new GovernorOptions { RawRetentionDays = days };

        if (valid)
        {
            options.Validate();
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        }
    }
}

public class StorageGovernorTests
{
    private static readonly DateTime Noon = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static DateOnly Day(int offsetFromToday) => DateOnly.FromDateTime(Noon).AddDays(offsetFromToday);

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class FakeState : IIngestState
    {
        public Dictionary<string, IngestStateEntry> Values { get; } = new(StringComparer.Ordinal);

        public bool FailWrites { get; set; }

        public Task<IngestStateEntry?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(name, out var entry) ? entry : null);

        public Task SetAsync(string name, string value, DateTime at, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("state is down");
            }

            Values[name] = new IngestStateEntry(value, at);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : IGovernorStore
    {
        private const long Gb = 1024L * 1024 * 1024;

        public long UsedBytes { get; set; } = 10 * Gb;

        public long? MaxBytes { get; set; } = 100 * Gb;

        public DateOnly? Oldest { get; set; }

        public List<DateOnly> Boundaries { get; set; } = [];

        public List<DateOnly> Truncated { get; } = [];

        public Exception? FailTruncate { get; set; }

        public Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StorageUsage(UsedBytes, UsedBytes + Gb, MaxBytes));

        public Task<DateOnly?> GetOldestDataDayAsync(CancellationToken cancellationToken = default) => Task.FromResult(Oldest);

        public Task<IReadOnlyList<DateOnly>> GetBoundariesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DateOnly>>([.. Boundaries]);

        public Task TruncateDayAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            if (FailTruncate is not null)
            {
                throw FailTruncate;
            }

            Truncated.Add(day);
            if (Oldest == day)
            {
                Oldest = Boundaries.Where(b => b > day).Select(b => (DateOnly?)b).FirstOrDefault();
            }

            return Task.CompletedTask;
        }
    }

    private static List<DateOnly> Days(int fromOffset, int toOffset) => [.. Enumerable.Range(fromOffset, toOffset - fromOffset + 1).Select(Day)];

    private static (StorageGovernor Governor, FakeStore Store, FakeState State, FixedTime Time) Rig(Action<FakeStore>? setup = null, int retentionDays = 30)
    {
        var store = new FakeStore { Boundaries = Days(-40, 7), Oldest = Day(-35) };
        setup?.Invoke(store);
        var state = new FakeState();
        var time = new FixedTime(Noon);
        return (new StorageGovernor(store, state, new GovernorOptions { RawRetentionDays = retentionDays }, time), store, state, time);
    }

    // ---- retention ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetentionDropsEveryDayOlderThanTheWindowOldestFirstAndKeepsTheRest()
    {
        var (governor, store, state, _) = Rig();

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Days(-35, -31), store.Truncated);   // today is day 0, so day -30 is the first one kept
        Assert.Equal(store.Truncated, report.Dropped);
        Assert.Null(report.Problem);
        Assert.Equal("2026-11-20", state.Values[GovernorStateKeys.RetentionDay].Value);
    }

    [Fact]
    public async Task RetentionRunsOnceADayAndAgainTheNextDay()
    {
        var (governor, store, _, time) = Rig();
        await governor.CheckAsync(TestContext.Current.CancellationToken);
        store.Truncated.Clear();
        store.Oldest = Day(-34);

        var again = await governor.CheckAsync(TestContext.Current.CancellationToken);
        time.Now = Noon.AddDays(1);
        store.Oldest = Day(-30);
        var tomorrow = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(again.Dropped);
        Assert.Equal([Day(-30)], tomorrow.Dropped);
    }

    [Fact]
    public async Task RetentionWaitsForItsTimeOfDay()
    {
        var (governor, store, state, time) = Rig();
        time.Now = new DateTime(2026, 11, 20, 0, 10, 0, DateTimeKind.Utc);

        var early = await governor.CheckAsync(TestContext.Current.CancellationToken);
        time.Now = new DateTime(2026, 11, 20, 0, 30, 0, DateTimeKind.Utc);
        var due = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(early.Dropped);
        Assert.NotEmpty(due.Dropped);
        Assert.Equal(Days(-35, -31), store.Truncated);
        Assert.True(state.Values.ContainsKey(GovernorStateKeys.RetentionDay));
    }

    [Fact]
    public async Task NothingOlderThanTheWindowMeansNothingDroppedButTheDayIsStillMarkedDone()
    {
        var (governor, store, state, _) = Rig(s => s.Oldest = Day(-12));

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Truncated);
        Assert.Empty(report.Dropped);
        Assert.Equal("2026-11-20", state.Values[GovernorStateKeys.RetentionDay].Value);
    }

    [Fact]
    public async Task AnEmptyDatabaseDropsNothing()
    {
        var (governor, store, _, _) = Rig(s => s.Oldest = null);

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Truncated);
        Assert.Null(report.Problem);
    }

    [Fact]
    public async Task DataOlderThanTheFirstBoundaryIsReportedNotSilentlyKept()
    {
        var (governor, store, _, _) = Rig(s =>
        {
            s.Boundaries = Days(-33, 7);
            s.Oldest = Day(-36);
        });

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Contains("older than the first partition boundary", report.Problem, StringComparison.Ordinal);
        Assert.Equal(Days(-33, -31), store.Truncated);   // the days that do have a partition are still dropped
    }

    [Fact]
    public async Task ADayThatCannotBeDroppedIsReportedAndRetentionIsRetriedAtTheNextCheck()
    {
        var (governor, store, state, _) = Rig(s => s.FailTruncate = new InvalidOperationException("deadlock victim"));

        var failed = await governor.CheckAsync(TestContext.Current.CancellationToken);
        var recorded = state.Values[GovernorStateKeys.Last].Value;
        store.FailTruncate = null;
        var retried = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("deadlock victim", failed.Problem);
        Assert.StartsWith("error", recorded, StringComparison.Ordinal);
        Assert.Equal(Days(-35, -31), retried.Dropped);
        Assert.Null(retried.Problem);
    }

    // ---- the size budget ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnderEightyPercentNothingHappensAndEightyWarns()
    {
        const long Gb = 1024L * 1024 * 1024;
        var (governor, store, _, _) = Rig(s =>
        {
            s.Oldest = Day(-10);
            s.UsedBytes = 79 * Gb;
        });

        var ok = await governor.CheckAsync(TestContext.Current.CancellationToken);
        store.UsedBytes = 85 * Gb;
        var warn = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal((BudgetLevel.Ok, 79), (ok.Level, ok.UsedPercent));
        Assert.Equal((BudgetLevel.Warn, 85), (warn.Level, warn.UsedPercent));
        Assert.Empty(store.Truncated);
    }

    [Fact]
    public async Task AtNinetyPercentTheOldestRawDayIsDroppedOnePerCheck()
    {
        const long Gb = 1024L * 1024 * 1024;
        var (governor, store, state, _) = Rig(s =>
        {
            s.Oldest = Day(-10);
            s.UsedBytes = 95 * Gb;
        });
        await state.SetAsync(GovernorStateKeys.RetentionDay, "2026-11-20", Noon, TestContext.Current.CancellationToken);

        var first = await governor.CheckAsync(TestContext.Current.CancellationToken);
        var second = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Day(-10)], first.Dropped);
        Assert.Equal([Day(-9)], second.Dropped);
        Assert.Equal(BudgetLevel.Shed, first.Level);
        Assert.Null(first.Problem);
    }

    [Fact]
    public async Task TheBudgetNeverDropsTheNewestThreeDaysAndSaysSo()
    {
        const long Gb = 1024L * 1024 * 1024;
        var (governor, store, state, _) = Rig(s =>
        {
            s.Oldest = Day(-3);
            s.UsedBytes = 99 * Gb;
        });
        await state.SetAsync(GovernorStateKeys.RetentionDay, "2026-11-20", Noon, TestContext.Current.CancellationToken);

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Truncated);
        Assert.Contains("nothing more can be dropped", report.Problem, StringComparison.Ordinal);
        Assert.StartsWith("error used=99%", state.Values[GovernorStateKeys.Last].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBudgetDoesNotDropAgainInTheCheckWhereRetentionAlreadyDropped()
    {
        const long Gb = 1024L * 1024 * 1024;
        var (governor, store, _, _) = Rig(s => s.UsedBytes = 95 * Gb);

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Days(-35, -31), report.Dropped);   // retention's days only: the freed pages need a check to show
        Assert.Equal(store.Truncated, report.Dropped);
    }

    [Fact]
    public async Task ADayWithoutItsOwnPartitionCannotBeShedAndTheReportSaysWhy()
    {
        const long Gb = 1024L * 1024 * 1024;
        var (governor, store, state, _) = Rig(s =>
        {
            s.Oldest = Day(-10);
            s.Boundaries = Days(-5, 7);
            s.UsedBytes = 95 * Gb;
        });
        await state.SetAsync(GovernorStateKeys.RetentionDay, "2026-11-20", Noon, TestContext.Current.CancellationToken);

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Truncated);
        Assert.Contains("no partition boundary", report.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCapMeansNoBudgetAction()
    {
        var (governor, store, state, _) = Rig(s =>
        {
            s.MaxBytes = null;
            s.Oldest = Day(-10);
        });
        await state.SetAsync(GovernorStateKeys.RetentionDay, "2026-11-20", Noon, TestContext.Current.CancellationToken);

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal((BudgetLevel.Ok, (int?)null), (report.Level, report.UsedPercent));
        Assert.Empty(store.Truncated);
        Assert.Equal("ok used=-% dropped=", state.Values[GovernorStateKeys.Last].Value);
    }

    // ---- reporting and robustness ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryCheckRecordsWhatItDidWithItsOwnTime()
    {
        var (governor, _, state, _) = Rig();

        await governor.CheckAsync(TestContext.Current.CancellationToken);

        var last = state.Values[GovernorStateKeys.Last];
        Assert.Equal("ok used=10% dropped=2026-10-16,2026-10-17,2026-10-18,2026-10-19,2026-10-20", last.Value);
        Assert.Equal(Noon, last.UpdatedAt);
    }

    [Fact]
    public async Task AFailureToRecordTheCheckDoesNotFailTheCheck()
    {
        var (governor, store, state, _) = Rig();
        state.FailWrites = true;

        var report = await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(report.Problem);   // the retention-day write failed
        Assert.Equal(Days(-35, -31), store.Truncated);
    }

    [Fact]
    public async Task AVeryLongProblemIsCutToWhatTheStateColumnHolds()
    {
        var (governor, _, state, _) = Rig(s => s.FailTruncate = new InvalidOperationException(new string('x', 1000)));

        await governor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(400, state.Values[GovernorStateKeys.Last].Value.Length);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var (governor, _, _, _) = Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new FakeStore { FailTruncate = new OperationCanceledException(cts.Token), Boundaries = Days(-40, 7), Oldest = Day(-35) };
        var cancelled = new StorageGovernor(store, new FakeState(), null, new FixedTime(Noon));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.CheckAsync(cts.Token));
        Assert.NotNull(governor);
    }

    [Fact]
    public void MissingPartsAndBadOptionsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new StorageGovernor(null!, new FakeState()));
        Assert.Throws<ArgumentNullException>(() => new StorageGovernor(new FakeStore(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageGovernor(new FakeStore(), new FakeState(), new GovernorOptions { RawRetentionDays = 2 }));
    }
}

public class GovernorStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private GovernorStore Store() => new(new DataSource(db.Options));

    [Fact]
    public async Task UsedSpaceIsLessThanTheFileAndTheCapIsTheConfiguredMaximum()
    {
        db.Require();

        var usage = await Store().GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.InRange(usage.UsedBytes, 1, usage.FileBytes);
        Assert.Equal(DatabaseFixture.MaxSizeBytes, usage.MaxBytes);
    }

    [Fact]
    public async Task OldestDataDayBoundariesAndTruncationWorkOnRealPartitions()
    {
        db.Require();
        var first = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        await ReaderSeed.DaysAsync(db, first, 5);
        var id = await ReaderSeed.MarketAsync(db, "GOV-A");
        Assert.Null(await Store().GetOldestDataDayAsync(TestContext.Current.CancellationToken));
        await ReaderSeed.TicksAsync(db, id, (first.AddDays(1).AddHours(5), 400_000), (first.AddDays(3).AddHours(2), 410_000));
        await ReaderSeed.TradesAsync(db, id, (first.AddDays(2).AddHours(1), "gov-1", 400_000, 1, false));

        var oldest = await Store().GetOldestDataDayAsync(TestContext.Current.CancellationToken);
        var boundaries = await Store().GetBoundariesAsync(TestContext.Current.CancellationToken);
        await Store().TruncateDayAsync(new DateOnly(2026, 3, 11), TestContext.Current.CancellationToken);
        var afterTruncate = await Store().GetOldestDataDayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 3, 11), oldest);
        Assert.Contains(new DateOnly(2026, 3, 12), boundaries);
        Assert.Equal(new DateOnly(2026, 3, 12), afterTruncate);   // the trade of the 12th is now the oldest row
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.ticker"));
    }

    [Fact]
    public async Task TruncatingADayWithoutItsOwnBoundaryIsRefused()
    {
        db.Require();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().TruncateDayAsync(new DateOnly(2001, 1, 1), TestContext.Current.CancellationToken));
    }
}
