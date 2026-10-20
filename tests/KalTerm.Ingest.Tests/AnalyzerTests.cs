using KalTerm.Data.Metrics;
using KalTerm.Ingest.Analytics;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest.Tests;

internal sealed class SettableTime(DateTime start) : TimeProvider
{
    public DateTime Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
}

internal sealed class FakeAnalyzer(string name = "fake", TimeSpan? interval = null) : IAnalyzer
{
    public string Name => name;

    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromMinutes(5);

    public int Runs { get; private set; }

    public List<DateTime> Starts { get; } = [];

    public Func<CancellationToken, Task<AnalyzerOutcome>> Body { get; set; } = _ => Task.FromResult(new AnalyzerOutcome(3, true, "three"));

    public Task<AnalyzerOutcome> RunAsync(AnalysisContext context, CancellationToken cancellationToken = default)
    {
        Runs++;
        Starts.Add(context.Now);
        return Body(cancellationToken);
    }
}

internal sealed class FakeAnalyzerStore : IAnalyzerStore
{
    public Dictionary<string, DateTime> Last { get; } = [];

    public HashSet<string> Locked { get; } = [];

    public List<string> Leases { get; } = [];

    public int Released { get; private set; }

    public List<(long Id, string Name, DateTime Started)> Begun { get; } = [];

    public Dictionary<long, (string Status, int? Rows, bool? Changed, string? Message)> Finished { get; } = [];

    public Dictionary<string, int> Versions { get; } = [];

    public List<DateTime> Prunes { get; } = [];

    public Exception? LastStartFails { get; set; }

    public Task<DateTime?> GetLastStartAsync(string name, CancellationToken cancellationToken = default) =>
        LastStartFails is not null ? Task.FromException<DateTime?>(LastStartFails) : Task.FromResult(Last.TryGetValue(name, out var at) ? (DateTime?)at : null);

    public Task SetLastStartAsync(string name, DateTime at, CancellationToken cancellationToken = default)
    {
        Last[name] = at;
        return Task.CompletedTask;
    }

    public Task<IAnalyzerLease?> TryLockAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Locked.Contains(name))
        {
            return Task.FromResult<IAnalyzerLease?>(null);
        }

        Leases.Add(name);
        return Task.FromResult<IAnalyzerLease?>(new Lease(this));
    }

    public Task<long> BeginRunAsync(string name, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        var id = Begun.Count + 1L;
        Begun.Add((id, name, startedAt));
        return Task.FromResult(id);
    }

    public Task FinishRunAsync(long runId, DateTime finishedAt, string status, int? rows, bool? changed, string? message, CancellationToken cancellationToken = default)
    {
        Finished[runId] = (status, rows, changed, message);
        return Task.CompletedTask;
    }

    public Task BumpVersionAsync(string name, DateTime at, CancellationToken cancellationToken = default)
    {
        Versions[name] = Versions.GetValueOrDefault(name) + 1;
        return Task.CompletedTask;
    }

    public Task<int> PruneRunsAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        Prunes.Add(olderThan);
        return Task.FromResult(0);
    }

    private sealed class Lease(FakeAnalyzerStore owner) : IAnalyzerLease
    {
        public ValueTask DisposeAsync()
        {
            owner.Released++;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeAnalyzerRunner : IAnalyzerRunner
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

public class AnalyzerRunnerTests
{
    private static readonly DateTime Start = new(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly SettableTime _time = new(Start);
    private readonly FakeAnalyzerStore _store = new();
    private readonly ListLogger _log = new();

    private AnalyzerRunner Runner(IReadOnlyList<IAnalyzer> analyzers, AnalyzerRunnerOptions? options = null) => new(analyzers, _store, options, _time, _log);

    [Fact]
    public async Task ADueAnalyzerRunsIsRecordedAndBumpsItsCounterWhenItsResultsChanged()
    {
        var analyzer = new FakeAnalyzer();

        var ran = await Runner([analyzer]).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, ran);
        Assert.Equal([Start], analyzer.Starts);
        Assert.Equal(Start, _store.Last["fake"]);
        Assert.Equal(("ok", 3, true, "three"), _store.Finished[1]);
        Assert.Equal(1, _store.Versions["fake"]);
        Assert.Equal(1, _store.Released);
    }

    [Fact]
    public async Task AnAnalyzerThatFoundNothingNewDoesNotBumpItsCounter()
    {
        var analyzer = new FakeAnalyzer { Body = _ => Task.FromResult(new AnalyzerOutcome(3, false)) };

        await Runner([analyzer]).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_store.Versions);
        Assert.Equal("ok", _store.Finished[1].Status);
    }

    [Fact]
    public async Task AnAnalyzerRunsAgainOnlyOnceItsIntervalHasPassed()
    {
        var analyzer = new FakeAnalyzer(interval: TimeSpan.FromMinutes(5));
        var runner = Runner([analyzer]);
        await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        _time.Now = Start.AddMinutes(4);
        var early = await runner.RunOnceAsync(TestContext.Current.CancellationToken);
        _time.Now = Start.AddMinutes(5);
        var due = await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0, 1), (early, due));
        Assert.Equal([Start, Start.AddMinutes(5)], analyzer.Starts);
    }

    [Fact]
    public async Task AnAnalyzerWhoseLockIsHeldElsewhereIsSkipped()
    {
        var analyzer = new FakeAnalyzer();
        _store.Locked.Add("fake");

        var ran = await Runner([analyzer]).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, ran);
        Assert.Equal(0, analyzer.Runs);
        Assert.Empty(_store.Begun);
        Assert.Contains(_log.Lines, l => l.Contains("already running elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureIsRecordedAndLoggedNotThrownAndTheLockIsStillReleased()
    {
        var analyzer = new FakeAnalyzer { Body = _ => Task.FromException<AnalyzerOutcome>(new InvalidOperationException("broke")) };
        var second = new FakeAnalyzer("second");

        var ran = await Runner([analyzer, second]).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, ran);   // the one after the broken one still ran
        Assert.Equal(("failed", null, null, "broke"), _store.Finished[1]);
        Assert.DoesNotContain("fake", _store.Versions.Keys);
        Assert.Equal(2, _store.Released);
        Assert.Contains(_log.Lines, l => l.Contains("failed: broke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABrokenAnalyzerIsNotRetriedBeforeItsIntervalHasPassed()
    {
        var analyzer = new FakeAnalyzer { Body = _ => Task.FromException<AnalyzerOutcome>(new InvalidOperationException("broke")) };
        var runner = Runner([analyzer]);
        await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        _time.Now = Start.AddMinutes(1);
        await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, analyzer.Runs);
    }

    [Fact]
    public async Task AnAnalyzerThatOutlivesTheTimeoutIsCancelledAndRecordedAsTimedOut()
    {
        var analyzer = new FakeAnalyzer { Body = async token => { await Task.Delay(Timeout.Infinite, token); return new AnalyzerOutcome(0, false); } };

        await Runner([analyzer], new AnalyzerRunnerOptions { Timeout = TimeSpan.FromMilliseconds(50) }).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("timeout", _store.Finished[1].Status);
        Assert.Contains("cancelled after", _store.Finished[1].Message, StringComparison.Ordinal);
        Assert.Equal(1, _store.Released);
    }

    [Fact]
    public async Task AStopWhileAnAnalyzerRunsIsRecordedAndPropagates()
    {
        using var stop = new CancellationTokenSource();
        var analyzer = new FakeAnalyzer { Body = async token => { await stop.CancelAsync(); await Task.Delay(Timeout.Infinite, token); return new AnalyzerOutcome(0, false); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner([analyzer]).RunOnceAsync(stop.Token));

        Assert.Equal(("failed", null, null, "stopped"), _store.Finished[1]);
        Assert.Equal(1, _store.Released);
    }

    [Fact]
    public async Task AFailingStoreIsLoggedAndTheOtherAnalyzersStillRun()
    {
        var first = new FakeAnalyzer();
        _store.LastStartFails = new InvalidOperationException("database down");

        var ran = await Runner([first]).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, ran);
        Assert.Contains(_log.Lines, l => l.Contains("bookkeeping failed: database down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRunHistoryIsPrunedAtMostOnceAnHour()
    {
        var runner = Runner([new FakeAnalyzer()], new AnalyzerRunnerOptions { RunRetention = TimeSpan.FromDays(7) });

        await runner.RunOnceAsync(TestContext.Current.CancellationToken);
        _time.Now = Start.AddMinutes(30);
        await runner.RunOnceAsync(TestContext.Current.CancellationToken);
        _time.Now = Start.AddMinutes(61);
        await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Start.AddDays(-7), Start.AddMinutes(61).AddDays(-7)], _store.Prunes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dash-ed")]
    public void AnAnalyzerNeedsAUsableName(string name)
    {
        Assert.Throws<ArgumentException>(() => Runner([new FakeAnalyzer(name)]));
    }

    [Fact]
    public void NamesMustBeUniqueAndTheIntervalPositiveAndTheOptionsUsable()
    {
        Assert.Throws<ArgumentException>(() => Runner([new FakeAnalyzer("a"), new FakeAnalyzer("a")]));
        Assert.Throws<ArgumentException>(() => Runner([new FakeAnalyzer("a", TimeSpan.Zero)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Runner([], new AnalyzerRunnerOptions { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Runner([], new AnalyzerRunnerOptions { RunRetention = TimeSpan.Zero }));
    }
}

public class MarketMetricsAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private sealed class FakeBatchReader(params IReadOnlyList<MarketInputs>[] batches) : IMetricsBatchReader
    {
        public async Task<int> ReadAsync(DateTime asOf, Func<IReadOnlyList<MarketInputs>, Task> onBatch, CancellationToken cancellationToken = default)
        {
            foreach (var batch in batches)
            {
                await onBatch(batch);
            }

            return batches.Sum(b => b.Count);
        }
    }

    private sealed class FakeStore : IMarketMetricsStore
    {
        public List<IReadOnlyList<(int MarketId, MarketMetrics Metrics)>> Upserts { get; } = [];

        public List<IReadOnlyCollection<int>> Kept { get; } = [];

        public int Changed { get; set; } = 2;

        public int Deleted { get; set; }

        public Task<int> UpsertAsync(IReadOnlyList<(int MarketId, MarketMetrics Metrics)> rows, DateTime changedAt, CancellationToken cancellationToken = default)
        {
            Upserts.Add(rows);
            return Task.FromResult(Changed);
        }

        public Task<int> DeleteOthersAsync(IReadOnlyCollection<int> keep, CancellationToken cancellationToken = default)
        {
            Kept.Add(keep);
            return Task.FromResult(Deleted);
        }
    }

    private static MarketInputs Market(int id, long volume = 400, decimal pv = 220_000_000m) =>
        new(id, new MetricsInputs(Now, Now.AddMinutes(-2), 560_000, 550_000, 570_000, 9_900, 2, volume, pv, null, [], false));

    [Fact]
    public async Task EachBatchIsComputedByTheCalculatorAndWrittenAndTheRestIsDeleted()
    {
        var store = new FakeStore { Deleted = 1 };
        var analyzer = new MarketMetricsAnalyzer(new FakeBatchReader([Market(1), Market(2)], [Market(9)]), store);

        var outcome = await analyzer.RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Upserts.Count);
        Assert.Equal([1, 2], store.Upserts[0].Select(r => r.MarketId));
        Assert.Equal(550_000L, store.Upserts[0][0].Metrics.Vwap24hE6);
        Assert.Equal(20_000L, store.Upserts[0][0].Metrics.SpreadE6);
        Assert.Equal([1, 2, 9], store.Kept.Single().Order());
        Assert.Equal(new AnalyzerOutcome(3, true, "4 written, 1 removed"), outcome);
    }

    [Fact]
    public async Task NothingChangedMeansNoChangeSignal()
    {
        var store = new FakeStore { Changed = 0, Deleted = 0 };

        var outcome = await new MarketMetricsAnalyzer(new FakeBatchReader([Market(1)]), store).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.False(outcome.Changed);
    }

    [Fact]
    public async Task OnlyRemovalsAlsoCountAsAChange()
    {
        var store = new FakeStore { Changed = 0, Deleted = 4 };

        var outcome = await new MarketMetricsAnalyzer(new FakeBatchReader(), store).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.True(outcome.Changed);
        Assert.Equal(0, outcome.Rows);
    }

    [Fact]
    public void ItIsCalledMarketMetricsAndRunsEveryFiveMinutesUnlessToldOtherwise()
    {
        var analyzer = new MarketMetricsAnalyzer(new FakeBatchReader(), new FakeStore());

        Assert.Equal(("market_metrics", TimeSpan.FromMinutes(5)), (analyzer.Name, analyzer.Interval));
        Assert.Equal(TimeSpan.FromMinutes(1), new MarketMetricsAnalyzer(new FakeBatchReader(), new FakeStore(), TimeSpan.FromMinutes(1)).Interval);
    }
}
