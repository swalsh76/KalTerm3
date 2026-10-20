using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.State;
using KalTerm.Data.Writing;
using KalTerm.Ingest.Books;
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
        BookPublishInterval = TimeSpan.FromMilliseconds(20),
        GovernorCheckInterval = TimeSpan.FromMilliseconds(40),
        BackfillInterval = TimeSpan.FromMilliseconds(30),
        BackupCheckInterval = TimeSpan.FromMilliseconds(30),
        CandleInterval = TimeSpan.FromMilliseconds(30),
        Schedule = now => (now.AddMilliseconds(30), PassKind.Incremental),
    };

    /// <summary>Everything a runner needs, with the clock real and every interval tiny.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _listings;

        public Rig(Func<int, Task<IStreamSession>> open, IEnumerable<Market>? catalog = null, Exception? firstListingFails = null, IngestRunnerOptions? options = null, IBookKeeper? books = null, KalTerm.Data.Governor.IStorageGovernor? governor = null, KalTerm.Ingest.Backfill.ITradeBackfiller? backfiller = null, KalTerm.Backup.IBackupJob? backup = null, KalTerm.Ingest.Candles.ICandleJob? candles = null)
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
                Log,
                books,
                governor,
                backfiller,
                backup,
                candles);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { BookPublishInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { GovernorCheckInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { BackfillInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { BackupCheckInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestRunnerOptions { CandleInterval = TimeSpan.Zero }.Validate());
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

    // ---- order books ----------------------------------------------------------------------------------------------------------------

    private sealed class FakeKeeper : IBookKeeper
    {
        private readonly Lock _sync = new();
        private int _publishes;

        public List<string> Calls { get; } = [];

        public List<IStreamSession> Sessions { get; } = [];

        public int Publishes => Volatile.Read(ref _publishes);

        public Exception? PublishFails { get; set; }

        public bool Stopped { get; private set; }

        public BookKeeperStats Stats => new(3, 2, 1, 4, 5, 6, 7, 8);

        public void SessionOpened(IStreamSession session)
        {
            lock (_sync)
            {
                Calls.Add("opened");
                Sessions.Add(session);
            }
        }

        public void SessionClosed()
        {
            lock (_sync)
            {
                Calls.Add("closed");
            }
        }

        public void Handle(KalTerm.Kalshi.Models.WsMessage message)
        {
            lock (_sync)
            {
                Calls.Add("handle:" + message.Type);
            }
        }

        public Task PublishAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _publishes);
            return PublishFails is null ? Task.CompletedTask : Task.FromException(PublishFails);
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public string[] CallList()
        {
            lock (_sync)
            {
                return [.. Calls];
            }
        }
    }

    [Fact]
    public async Task TheBookKeeperIsToldWhenAStreamOpensAndClosesGetsItsMessagesIsPublishedAndStoppedAtShutdown()
    {
        var keeper = new FakeKeeper();
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions), books: keeper);
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        At(sessions, 0).Push(Message("orderbook_snapshot", """{"market_ticker":"A-1","yes_dollars_fp":[],"no_dollars_fp":[]}"""));
        await Wait.UntilAsync(() => keeper.CallList().Contains("handle:orderbook_snapshot"), "the book message to be handed over");
        await Wait.UntilAsync(() => keeper.Publishes >= 3, "several publishes");
        At(sessions, 0).Fail(new Kalshi.WebSocket.KalshiWsException("lost"));
        await Wait.UntilAsync(() => rig.Source.Opens >= 2, "the stream to be replaced");
        await rig.StopAsync();

        Assert.Same(At(sessions, 0), keeper.Sessions[0]);
        var calls = keeper.CallList();
        Assert.Equal("opened", calls[0]);
        Assert.True(calls.IndexOf("closed") > calls.IndexOf("handle:orderbook_snapshot"));
        Assert.Equal(2, calls.Count(c => c == "opened"));
        Assert.True(keeper.Stopped);
        Assert.Equal(8, rig.Runner.Status.Books!.Published);
        Assert.Contains(rig.Log.Lines, l => l.Contains("books=2/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedBookPublishIsLoggedAndTheRunCarriesOn()
    {
        var keeper = new FakeKeeper { PublishFails = new InvalidOperationException("database is down") };
        using var rig = new Rig(Sessions([]), books: keeper);
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => keeper.Publishes >= 3 && rig.Log.Lines.Any(l => l.Contains("order book publish failed", StringComparison.Ordinal)), "failed publishes to be logged");
        keeper.PublishFails = null;
        var before = keeper.Publishes;
        await Wait.UntilAsync(() => keeper.Publishes > before + 2, "publishing to carry on");
        await rig.StopAsync();
    }

    [Fact]
    public async Task WithoutABookKeeperThereIsNoBookLoopAndNoBookStatus()
    {
        using var rig = new Rig(Sessions([]));
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the first stream to open");
        await rig.StopAsync();

        Assert.Null(rig.Runner.Status.Books);
    }

    [Fact]
    public async Task TheStorageGovernorIsCheckedRightAwayAndThenOnItsInterval()
    {
        var governor = new FakeGovernor();
        using var rig = new Rig(Sessions([]), governor: governor);

        rig.Start();
        await Wait.UntilAsync(() => governor.Checks >= 3, "three governor checks");
        await rig.StopAsync();

        Assert.True(governor.Checks >= 3);
    }

    [Fact]
    public async Task AGovernorThatFailsIsLoggedAndTheRunCarriesOn()
    {
        var governor = new FakeGovernor { Failure = new InvalidOperationException("governor broke") };
        var sessions = new List<ScriptedSession>();
        using var rig = new Rig(Sessions(sessions), governor: governor);
        await rig.SeedAsync("A-1");

        rig.Start();
        await Wait.UntilAsync(() => governor.Checks >= 2, "two failing checks");
        await Wait.UntilAsync(() => rig.Source.Opens >= 1, "the stream to open");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("governor broke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBackfillRunsGapAfterGapWithoutWaitingThenWaitsForMore()
    {
        var backfiller = new FakeBackfiller { GapsToRecover = 3 };
        using var rig = new Rig(Sessions([]), options: Fast with { BackfillInterval = TimeSpan.FromMinutes(5) }, backfiller: backfiller);

        rig.Start();
        await Wait.UntilAsync(() => backfiller.Calls >= 4, "three gaps and then one empty look");
        await rig.StopAsync();

        Assert.Equal(4, backfiller.Calls);   // after the empty look it waits five minutes
    }

    [Fact]
    public async Task ABackfillThatFailsIsLoggedAndTheRunCarriesOn()
    {
        var backfiller = new FakeBackfiller { Failure = new InvalidOperationException("backfill broke") };
        using var rig = new Rig(Sessions([]), backfiller: backfiller);

        rig.Start();
        await Wait.UntilAsync(() => backfiller.Calls >= 2, "two failing runs");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("backfill broke", StringComparison.Ordinal));
        Assert.True(rig.State.Writes(IngestStateKeys.Running) > 0);
    }

    [Fact]
    public async Task TheBackupJobLooksAtTheClockOnItsIntervalAndAFailureNeverStopsTheRun()
    {
        var job = new FakeBackupJob { Failure = new InvalidOperationException("backup job broke") };
        using var rig = new Rig(Sessions([]), backup: job);

        rig.Start();
        await Wait.UntilAsync(() => job.Calls >= 3, "three looks at the backup schedule");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("backup job broke", StringComparison.Ordinal));
        Assert.True(rig.State.Writes(IngestStateKeys.Running) > 0);
    }

    [Fact]
    public async Task TheCandleJobRunsOnItsIntervalAndAFailureNeverStopsTheRun()
    {
        var job = new FakeCandleJob { Failure = new InvalidOperationException("candles broke") };
        using var rig = new Rig(Sessions([]), candles: job);

        rig.Start();
        await Wait.UntilAsync(() => job.Calls >= 3, "three runs of the candle job");
        await rig.StopAsync();

        Assert.Contains(rig.Log.Lines, l => l.Contains("candles broke", StringComparison.Ordinal));
        Assert.True(rig.State.Writes(IngestStateKeys.Running) > 0);
    }
}
