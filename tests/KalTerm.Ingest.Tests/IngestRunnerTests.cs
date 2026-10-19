using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.State;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.IngestTestKit;
using static KalTerm.Ingest.Tests.StreamTestKit;

namespace KalTerm.Ingest.Tests;

public class IngestRunnerTests
{
    private static readonly IngestRunnerOptions Fast = new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        PartitionCheckInterval = TimeSpan.FromMilliseconds(50),
        StatusInterval = TimeSpan.FromMilliseconds(50),
        StreamRetryBase = TimeSpan.FromMilliseconds(10),
        StreamRetryMax = TimeSpan.FromMilliseconds(40),
        StopTimeout = TimeSpan.FromSeconds(5),
        Schedule = now => (now.AddMilliseconds(30), PassKind.Incremental),
    };

    /// <summary>Everything a runner needs, with the clock real and every interval tiny.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _listings;

        public Rig(Func<int, Task<IStreamSession>> open, IEnumerable<Market>? catalog = null, Exception? firstListingFails = null, IngestRunnerOptions? options = null)
        {
            var markets = catalog?.ToList() ?? [];
            Source = new ScriptedSource(open);
            Discovery = new MarketDiscovery(
                Store,
                Ids,
                _ => List(markets, Interlocked.Increment(ref _listings) == 1 ? firstListingFails : null),
                TimeProvider.System,
                NullLogger.Instance,
                listUpdatedSince: (_, _) => List(markets));
            Tickers = new BulkWriter<TickerRow>(TickerSink, TickerRowMapper.Instance, new BulkWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(20) });
            Trades = new BulkWriter<TradeRow>(TradeSink, TradeRowMapper.Instance, new BulkWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(20) });
            Runner = new IngestRunner(
                Store,
                Ids,
                Discovery,
                Source,
                Tickers,
                Trades,
                Gaps,
                State,
                Partitions,
                options ?? Fast,
                new StreamIngestorOptions { FlushInterval = TimeSpan.FromMilliseconds(20) },
                TimeProvider.System,
                Log);
        }

        public FakeMarketStore Store { get; } = new();

        public MarketIdMap Ids { get; } = new();

        public ScriptedSource Source { get; }

        public MarketDiscovery Discovery { get; }

        public RecordingSink<TickerRow> TickerSink { get; } = new();

        public RecordingSink<TradeRow> TradeSink { get; } = new();

        public BulkWriter<TickerRow> Tickers { get; }

        public BulkWriter<TradeRow> Trades { get; }

        public FakeGapLog Gaps { get; } = new();

        public FakeIngestState State { get; } = new();

        public FakePartitions Partitions { get; } = new();

        public ListLogger Log { get; } = new();

        public IngestRunner Runner { get; }

        public Task? Run { get; private set; }

        /// <summary>Puts markets in the catalog, so start-up loads their ids instead of running a first pass.</summary>
        public async Task SeedAsync(params string[] tickers) =>
            await Store.UpsertAsync([.. tickers.Select(t => MarketMapper.ToRecord(Open(t)))], DateTime.UtcNow, TestContext.Current.CancellationToken);

        public void Start() => Run = Runner.RunAsync(_stop.Token);

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await Run!;
        }

        public void Dispose() => _stop.Dispose();
    }

    private static Func<int, Task<IStreamSession>> Sessions(List<ScriptedSession> into) => _ =>
    {
        var session = new ScriptedSession();
        lock (into)
        {
            into.Add(session);
        }

        return Task.FromResult<IStreamSession>(session);
    };

    private static ScriptedSession At(List<ScriptedSession> sessions, int index)
    {
        lock (sessions)
        {
            return sessions[index];
        }
    }

    [Fact]
    public async Task StartUpAddsPartitionsLoadsTheIdsAndMarksTheRunRunning()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1", "B-2");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        await rig.StopAsync();

        var first = rig.Partitions.Calls.First();
        Assert.Equal(7, first.Days);
        Assert.InRange(first.Today.DayNumber, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - 1, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber + 1);
        Assert.True(rig.Ids.TryGetId("B-2", out _));
        Assert.True(rig.State.Writes(IngestStateKeys.Running) > 0);
        Assert.Equal(IngestStateKeys.Stopped, rig.State.Current(IngestStateKeys.Run)!.Value);
    }

    [Fact]
    public async Task AnEmptyCatalogGetsAFullPassBeforeTheFirstStream()
    {
        var idsAtOpen = -1;
        Rig rig = null!;
        rig = new Rig(
            _ =>
            {
                idsAtOpen = rig.Ids.Count;
                return Task.FromResult<IStreamSession>(new ScriptedSession());
            },
            [Open("N-1"), Open("N-2"), Open("N-3")]);

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        await rig.StopAsync();

        Assert.Equal(3, idsAtOpen);
    }

    [Theory]
    [InlineData("running", -10, "did not stop cleanly")]
    [InlineData("stopped", -10, "stopped cleanly")]
    public async Task TheTimeSinceTheLastRunIsLoggedAsDowntime(string previous, int minutesAgo, string expected)
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");
        var before = DateTime.UtcNow.AddMinutes(minutesAgo);
        rig.State.Seed(IngestStateKeys.Run, previous, before);

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        await rig.StopAsync();

        var gap = Assert.Single(rig.Gaps.Stored, g => g.Kind == GapKinds.IngestDown);
        Assert.Equal(before, gap.StartedAt);
        Assert.InRange(gap.EndedAt!.Value, before, DateTime.UtcNow);
        Assert.Contains(expected, gap.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThereIsNoDowntimeToLogOnAFirstRunOrWhenTheStoredTimeIsInTheFuture(bool stateInTheFuture)
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");
        if (stateInTheFuture)
        {
            rig.State.Seed(IngestStateKeys.Run, IngestStateKeys.Stopped, DateTime.UtcNow.AddHours(1));
        }

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        await rig.StopAsync();

        Assert.DoesNotContain(rig.Gaps.Stored, g => g.Kind == GapKinds.IngestDown);
    }

    [Fact]
    public async Task MessagesBecomeRowsAndShuttingDownDrainsThem()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1", "B-2");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        var session = At(sessions, 0);
        session.Push(Ticker("A-1"));
        session.Push(Trade("B-2", "t-7"));
        session.Push(Ticker("UNKNOWN-9"));
        await Wait.UntilAsync(() => rig.Runner.Status.Ingest.UnknownMarkets == 1 && rig.Runner.Status.Ingest.Trades == 1, "the messages to be handled");
        await rig.StopAsync();

        Assert.Equal([1], rig.TickerSink.Rows.Select(r => r.MarketId));
        Assert.Equal(["t-7"], rig.TradeSink.Rows.Select(r => r.TradeId));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task AStreamThatFailsIsReplacedAndTheOutageIsLogged()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        At(sessions, 0).Push(Ticker("A-1"));
        At(sessions, 0).Fail(new KalshiWsException("reconnect attempts exhausted"));
        await Wait.UntilAsync(() => rig.Source.Opens >= 2, "the second stream to open");
        At(sessions, 1).Push(Ticker("A-1"));
        await rig.StopAsync();

        Assert.Contains(rig.Gaps.Stored, g => g.Kind == GapKinds.WsLost && g.Detail == "reconnect attempts exhausted");
        var down = Assert.Single(rig.Gaps.Stored, g => g.Kind == GapKinds.WsDown);
        Assert.Contains("1 attempt", down.Detail, StringComparison.Ordinal);
        Assert.True(rig.Runner.Status.StreamRestarts >= 1);
        Assert.Equal(2, rig.TickerSink.Rows.Count);
    }

    [Fact]
    public async Task AStreamThatEndsNormallyWithoutAStopIsReplacedToo()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        At(sessions, 0).End();
        await Wait.UntilAsync(() => rig.Source.Opens >= 2, "the second stream to open");
        await rig.StopAsync();

        Assert.True(At(sessions, 0).Disposed);
    }

    [Fact]
    public async Task OpeningFailsAndRetriesWithGrowingWaitsUntilItWorks()
    {
        var sessions = new List<ScriptedSession>();
        Rig rig = null!;
        rig = new Rig(n => n <= 2 ? Task.FromException<IStreamSession>(new HttpRequestException("no route")) : Sessions(sessions)(n));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 3, "the third attempt");
        await rig.StopAsync();

        var down = Assert.Single(rig.Gaps.Stored, g => g.Kind == GapKinds.WsDown);
        Assert.Contains("2 attempt", down.Detail, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Lines, l => l.Contains("(attempt 1)", StringComparison.Ordinal));
        Assert.Contains(rig.Log.Lines, l => l.Contains("(attempt 2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStreamThatDeliveredDataResetsTheRetryWait()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        At(sessions, 0).Push(Ticker("A-1"));
        At(sessions, 0).Fail(new KalshiWsException("lost"));
        await Wait.UntilAsync(() => rig.Source.Opens >= 2, "the second stream to open");
        At(sessions, 1).Push(Ticker("A-1"));
        At(sessions, 1).Fail(new KalshiWsException("lost again"));
        await Wait.UntilAsync(() => rig.Source.Opens >= 3, "the third stream to open");
        await rig.StopAsync();

        Assert.Equal(2, rig.Log.Lines.Count(l => l.Contains("(attempt 1)", StringComparison.Ordinal)));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("(attempt 2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnexpectedErrorStopsTheRunButStillShutsDownInOrder()
    {
        using var rig = new Rig(_ => Task.FromException<IStreamSession>(new InvalidOperationException("a bug")));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Run!);

        Assert.Equal(IngestStateKeys.Stopped, rig.State.Current(IngestStateKeys.Run)!.Value);
        Assert.Contains(rig.Log.Lines, l => l.Contains("stopping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedStartUpIsThrownAndNothingStarts()
    {
        using var rig = new Rig(Sessions([]));
        rig.Partitions.FailFromCall = 1;

        rig.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Run!);

        Assert.Equal(0, rig.Source.Opens);
        Assert.Empty(rig.State.History);
    }

    [Fact]
    public async Task AFailedDiscoveryPassIsCountedAndTheRunCarriesOn()
    {
        using var rig = new Rig(Sessions([]), [Open("D-1")], firstListingFails: new HttpRequestException("listing failed"));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Runner.Status.DiscoveryFailures >= 1 && rig.Runner.Status.LastDiscovery is not null, "a failed pass and then a good one");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("discovery pass failed", StringComparison.Ordinal));
        Assert.NotNull(rig.Runner.Status.LastDiscoveryAt);
    }

    [Fact]
    public async Task TheHeartbeatPartitionCheckAndStatusLineKeepGoing()
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(
            () => rig.State.Writes(IngestStateKeys.Running) >= 4 && rig.Partitions.Calls.Count >= 3 && rig.Log.Lines.Any(l => l.Contains("status:", StringComparison.Ordinal)),
            "heartbeats, partition checks and a status line");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("connected=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingHeartbeatOrPartitionCheckIsLoggedAndRetried()
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.State.Writes(IngestStateKeys.Running) >= 1, "start-up");
        rig.State.FailWhenWriting = IngestStateKeys.Running;
        rig.Partitions.FailFromCall = 2;
        await Wait.UntilAsync(
            () => rig.Log.Lines.Any(l => l.Contains("heartbeat failed", StringComparison.Ordinal)) && rig.Log.Lines.Any(l => l.Contains("partitions failed", StringComparison.Ordinal)),
            "both failures to be logged");
        var writesWhileFailing = rig.State.Writes(IngestStateKeys.Running);
        rig.State.FailWhenWriting = null;
        await Wait.UntilAsync(() => rig.State.Writes(IngestStateKeys.Running) > writesWhileFailing, "the heartbeat to recover");
        await rig.StopAsync();
    }

    [Fact]
    public async Task ADatabaseThatCannotMarkTheRunStoppedDoesNotThrowAtShutdown()
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        rig.State.FailWhenWriting = IngestStateKeys.Stopped;
        await rig.StopAsync();

        Assert.Equal(IngestStateKeys.Running, rig.State.Current(IngestStateKeys.Run)!.Value);
        Assert.Contains(rig.Log.Lines, l => l.Contains("did not finish cleanly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusDescribesTheOpenStream()
    {
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions));
        await rig.SeedAsync("A-1");
        Assert.False(rig.Runner.Status.StreamConnected);

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1 && rig.Runner.Status.StreamConnected, "the stream to connect");
        var status = rig.Runner.Status;
        await rig.StopAsync();

        Assert.NotNull(status.Stream);
        Assert.NotEqual(default, status.StartedAt);
        Assert.False(rig.Runner.Status.StreamConnected);
    }

    [Fact]
    public void BadOptionsAndMissingPartsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { HeartbeatInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { PartitionCheckInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { StatusInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { StreamRetryBase = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { StopTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { StreamRetryBase = TimeSpan.FromSeconds(5), StreamRetryMax = TimeSpan.FromSeconds(1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { PartitionDaysAhead = 0 }.Validate());
        new IngestRunnerOptions().Validate();

        using var rig = new Rig(Sessions([]));
        _ = new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, rig.State, rig.Partitions);
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(null!, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, null!, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, null!, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, null!, rig.Tickers, rig.Trades, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, null!, rig.Trades, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, null!, rig.Gaps, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, null!, rig.State, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, null!, rig.Partitions));
        Assert.Throws<ArgumentNullException>(() => new IngestRunner(rig.Store, rig.Ids, rig.Discovery, rig.Source, rig.Tickers, rig.Trades, rig.Gaps, rig.State, null!));
    }
}
