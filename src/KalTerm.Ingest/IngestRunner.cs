using System.Net.Sockets;
using System.Net.WebSockets;
using KalTerm.Data;
using KalTerm.Data.Gaps;
using KalTerm.Data.Governor;
using KalTerm.Data.Markets;
using KalTerm.Data.State;
using KalTerm.Data.Writing;
using KalTerm.Ingest.Backfill;
using KalTerm.Ingest.Books;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest;

/// <summary>Tuning for <see cref="IngestRunner"/>.</summary>
public sealed record IngestRunnerOptions
{
    /// <summary>How often the run state is refreshed in the database; a crash loses at most this much of the downtime's start. Default 15 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many days past today get a partition boundary. Default 7.</summary>
    public int PartitionDaysAhead { get; init; } = 7;

    /// <summary>How often the boundaries are topped up while running. Default 1 hour.</summary>
    public TimeSpan PartitionCheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long the trade backfill waits when no gap is waiting. Default 30 seconds.</summary>
    public TimeSpan BackfillInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the storage governor checks retention and the size budget. Default 15 minutes.</summary>
    public TimeSpan GovernorCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How often a status line is logged. Default 1 minute.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The first wait before opening a new stream after one ended or failed; it doubles each attempt. Default 1 second.</summary>
    public TimeSpan StreamRetryBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between attempts to open a stream. Default 60 seconds.</summary>
    public TimeSpan StreamRetryMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often changed order books are written to the database. Default 1 second.</summary>
    public TimeSpan BookPublishInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest the final state write and gap flush wait at shutdown. Default 10 seconds.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>When the next discovery pass runs and which kind; <see langword="null"/> for <see cref="RefreshSchedule.NextPass"/>. A seam for tests.</summary>
    public Func<DateTimeOffset, (DateTimeOffset At, PassKind Kind)>? Schedule { get; init; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero || PartitionCheckInterval <= TimeSpan.Zero || GovernorCheckInterval <= TimeSpan.Zero || BackfillInterval <= TimeSpan.Zero || StatusInterval <= TimeSpan.Zero
            || StreamRetryBase <= TimeSpan.Zero || StopTimeout <= TimeSpan.Zero || BookPublishInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(IngestRunnerOptions), "every interval and timeout must be positive");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(StreamRetryMax, StreamRetryBase);
        ArgumentOutOfRangeException.ThrowIfLessThan(PartitionDaysAhead, 1);
    }
}

/// <summary>A snapshot of everything the runner counts, for the status line and, later, the API.</summary>
/// <param name="StartedAt">When this run started.</param>
/// <param name="StreamConnected">Whether a stream is open and its link is up.</param>
/// <param name="StreamRestarts">Times a stream ended or failed and was replaced.</param>
/// <param name="Stream">Queue health of the open stream, if there is one.</param>
/// <param name="Ingest">The ingestor's counters.</param>
/// <param name="Tickers">The ticker writer.</param>
/// <param name="Trades">The trade writer.</param>
/// <param name="LastDiscovery">The last completed discovery pass.</param>
/// <param name="LastDiscoveryAt">When it completed.</param>
/// <param name="DiscoveryFailures">Discovery passes that failed.</param>
/// <param name="Books">The order book keeper's counters, when books are kept.</param>
public sealed record IngestStatus(
    DateTimeOffset StartedAt,
    bool StreamConnected,
    long StreamRestarts,
    WsStats? Stream,
    StreamIngestorStats Ingest,
    BulkWriterStats Tickers,
    BulkWriterStats Trades,
    DiscoveryResult? LastDiscovery,
    DateTimeOffset? LastDiscoveryAt,
    long DiscoveryFailures,
    BookKeeperStats? Books = null);

/// <summary>
/// The ingest process, minus hosting. Starting: day partitions are added ahead, the market ids are loaded (a first full discovery pass if there
/// are none), the time since the last run is logged as an <c>ingest_down</c> gap, and the writers start. Running: the stream is read and replaced
/// whenever it ends, discovery follows <see cref="RefreshSchedule"/>, and a heartbeat keeps the run state fresh. Stopping (the token is cancelled):
/// the stream and discovery stop, the writers drain, the last gaps are stored, and the run state is marked stopped. A failure to start is
/// thrown; after that only a cancellation or an unexpected error ends the run.
/// </summary>
public sealed partial class IngestRunner
{
    private readonly IMarketStore _store;
    private readonly MarketIdMap _ids;
    private readonly MarketDiscovery _discovery;
    private readonly IStreamSource _source;
    private readonly BulkWriter<TickerRow> _tickers;
    private readonly BulkWriter<TradeRow> _trades;
    private readonly IGapLog _gaps;
    private readonly IIngestState _state;
    private readonly IPartitionMaintenance _partitions;
    private readonly IngestRunnerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly StreamIngestor _ingestor;
    private readonly IBookKeeper? _books;
    private readonly IStorageGovernor? _governor;
    private readonly ITradeBackfiller? _backfill;
    private long _restarts;
    private long _discoveryFailures;
    private DateTimeOffset _startedAt;
    private IStreamSession? _session;
    private DiscoveryResult? _lastDiscovery;
    private DateTimeOffset? _lastDiscoveryAt;

    /// <summary>Creates a runner. It starts and disposes the two writers.</summary>
    /// <param name="store">The market catalog.</param>
    /// <param name="ids">The ticker-to-id map the discovery fills and the ingestor reads.</param>
    /// <param name="discovery">Keeps the catalog current.</param>
    /// <param name="source">Opens the subscribed stream.</param>
    /// <param name="tickers">Writes ticker rows.</param>
    /// <param name="trades">Writes trade rows.</param>
    /// <param name="gaps">Stores gaps.</param>
    /// <param name="state">Stores the run state.</param>
    /// <param name="partitions">Keeps day partitions ahead of the clock.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="ingestorOptions">Tuning of the stream ingestor; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    /// <param name="books">Keeps and publishes the order books of the watch list; <see langword="null"/> when none are kept.</param>
    /// <param name="governor">Applies the retention and the size budget; <see langword="null"/> when none is kept.</param>
    /// <param name="backfiller">Recovers the trades of closed gaps from REST; <see langword="null"/> when none is kept.</param>
    public IngestRunner(
        IMarketStore store,
        MarketIdMap ids,
        MarketDiscovery discovery,
        IStreamSource source,
        BulkWriter<TickerRow> tickers,
        BulkWriter<TradeRow> trades,
        IGapLog gaps,
        IIngestState state,
        IPartitionMaintenance partitions,
        IngestRunnerOptions? options = null,
        StreamIngestorOptions? ingestorOptions = null,
        TimeProvider? time = null,
        ILogger? logger = null,
        IBookKeeper? books = null,
        IStorageGovernor? governor = null,
        ITradeBackfiller? backfiller = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tickers);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(partitions);
        _options = options ?? new IngestRunnerOptions();
        _options.Validate();
        _store = store;
        _ids = ids;
        _discovery = discovery;
        _source = source;
        _tickers = tickers;
        _trades = trades;
        _gaps = gaps;
        _state = state;
        _partitions = partitions;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _books = books;
        _governor = governor;
        _backfill = backfiller;
        _ingestor = new StreamIngestor(ids, tickers, trades, gaps, ingestorOptions, _time, _logger, books);
    }

    /// <summary>Everything the runner counts, right now.</summary>
    public IngestStatus Status
    {
        get
        {
            var session = _session;
            return new IngestStatus(
                _startedAt,
                session?.IsConnected ?? false,
                Interlocked.Read(ref _restarts),
                session?.Stats,
                _ingestor.Stats,
                _tickers.Stats,
                _trades.Stats,
                _lastDiscovery,
                _lastDiscoveryAt,
                Interlocked.Read(ref _discoveryFailures),
                _books?.Stats);
        }
    }

    /// <summary>Runs until <paramref name="stopToken"/> is cancelled, then shuts down in order.</summary>
    /// <param name="stopToken">Asks the runner to stop.</param>
    /// <returns>A task that completes when the shutdown is done.</returns>
    public async Task RunAsync(CancellationToken stopToken = default)
    {
        _startedAt = _time.GetUtcNow();
        try
        {
            await StartUpAsync(stopToken).ConfigureAwait(false);
        }
        catch
        {
            // Nothing is running; release the writers the caller handed over.
            await DisposeWritersAsync().ConfigureAwait(false);
            throw;
        }

        LogStarted(_logger, _ids.Count);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        List<Task> loops = [Guard(StreamLoopAsync, linked), Guard(DiscoveryLoopAsync, linked), Guard(MaintenanceLoopAsync, linked)];
        if (_books is not null)
        {
            loops.Add(Guard(BookLoopAsync, linked));
        }

        if (_backfill is not null)
        {
            loops.Add(Guard(BackfillLoopAsync, linked));
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // A requested stop.
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await ShutDownAsync().ConfigureAwait(false);
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is KalshiWsException or HttpRequestException or WebSocketException or SocketException or IOException or TimeoutException;

    private async Task StartUpAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await _partitions.EnsureAheadAsync(DateOnly.FromDateTime(now.UtcDateTime), _options.PartitionDaysAhead, cancellationToken).ConfigureAwait(false);
        await _ids.LoadAsync(_store, cancellationToken).ConfigureAwait(false);
        if (_ids.Count == 0)
        {
            // Without market ids every message would be an unknown market: build the catalog first.
            await _discovery.RunAsync(PassKind.Full, cancellationToken).ConfigureAwait(false);
        }

        var previous = await _state.GetAsync(IngestStateKeys.Run, cancellationToken).ConfigureAwait(false);
        if (previous is not null && now.UtcDateTime > previous.UpdatedAt)
        {
            var how = previous.Value == IngestStateKeys.Stopped ? "the previous run stopped cleanly" : "the previous run did not stop cleanly; from its last heartbeat";
            await _gaps.AppendAsync([new GapRecord(GapKinds.IngestDown, previous.UpdatedAt, now.UtcDateTime, how)], cancellationToken).ConfigureAwait(false);
        }

        await _state.SetAsync(IngestStateKeys.Run, IngestStateKeys.Running, now.UtcDateTime, cancellationToken).ConfigureAwait(false);
        _tickers.Start();
        _trades.Start();
    }

    /// <summary>Runs one loop; an unexpected failure stops the others too and surfaces from <see cref="RunAsync"/>.</summary>
    private async Task Guard(Func<CancellationToken, Task> loop, CancellationTokenSource linked)
    {
        try
        {
            await loop(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            LogLoopFailed(_logger, ex);
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StreamLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;      // the backoff exponent: back to 0 only once a stream has delivered data
        var failures = 0;     // streams that failed or ended since the last good one, for the gap's detail
        DateTimeOffset? downSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            IStreamSession? session = null;
            var messagesBefore = DataMessages();
            try
            {
                session = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (downSince is { } since)
                {
                    _ingestor.NoteGap(GapKinds.WsDown, since.UtcDateTime, _time.GetUtcNow().UtcDateTime, $"{failures} attempt(s) to open a stream failed or ended");
                    downSince = null;
                    failures = 0;
                }

                _session = session;
                _books?.SessionOpened(session);
                await _ingestor.RunAsync(session.Messages(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                LogStreamFailed(_logger, ex.Message);
            }
            finally
            {
                _session = null;
                _books?.SessionClosed();
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                if (DataMessages() != messagesBefore)
                {
                    attempt = 0;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            downSince ??= _time.GetUtcNow();
            failures++;
            Interlocked.Increment(ref _restarts);
            var wait = TimeSpan.FromSeconds(Math.Min(_options.StreamRetryMax.TotalSeconds, _options.StreamRetryBase.TotalSeconds * Math.Pow(2, Math.Min(attempt, 20))));
            attempt++;
            LogStreamRetry(_logger, attempt, wait);
            try
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Ticker and trade messages seen so far, including those for markets not in the catalog.</summary>
    private long DataMessages()
    {
        var stats = _ingestor.Stats;
        return stats.Tickers + stats.Trades + stats.UnknownMarkets;
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var (at, kind) = (_options.Schedule ?? RefreshSchedule.NextPass)(now);
            await Task.Delay(at - now, _time, cancellationToken).ConfigureAwait(false);
            try
            {
                _lastDiscovery = await _discovery.RunAsync(kind, cancellationToken).ConfigureAwait(false);
                _lastDiscoveryAt = _time.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The catalog is stale until the next tick; ingest carries on.
                Interlocked.Increment(ref _discoveryFailures);
                LogDiscoveryFailed(_logger, kind, ex.Message);
            }
        }
    }

    /// <summary>Writes the order books to the database every <see cref="IngestRunnerOptions.BookPublishInterval"/>; a failed write is logged and retried on the next tick.</summary>
    private async Task BookLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.BookPublishInterval, _time, cancellationToken).ConfigureAwait(false);
            await TryAsync("order book publish", () => _books!.PublishAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Recovers the trades of closed gaps, one gap after another, then waits <see cref="IngestRunnerOptions.BackfillInterval"/> for the next. It has its own loop because a long gap
    /// takes minutes and the maintenance loop must keep the heartbeat.
    /// </summary>
    private async Task BackfillLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BackfillOutcome? outcome = null;
            await TryAsync("trade backfill", async () => outcome = await _backfill!.RunOnceAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                await Task.Delay(_options.BackfillInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        var nextPartitions = _time.GetUtcNow() + _options.PartitionCheckInterval;
        var nextStatus = _time.GetUtcNow() + _options.StatusInterval;
        var nextGovernor = _time.GetUtcNow();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.HeartbeatInterval, _time, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            await TryAsync("heartbeat", () => _state.SetAsync(IngestStateKeys.Run, IngestStateKeys.Running, now.UtcDateTime, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (now >= nextPartitions)
            {
                nextPartitions = now + _options.PartitionCheckInterval;
                await TryAsync("partitions", () => _partitions.EnsureAheadAsync(DateOnly.FromDateTime(now.UtcDateTime), _options.PartitionDaysAhead, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            if (_governor is not null && now >= nextGovernor)
            {
                nextGovernor = now + _options.GovernorCheckInterval;
                await TryAsync("storage governor", () => _governor.CheckAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            if (now >= nextStatus)
            {
                nextStatus = now + _options.StatusInterval;
                var status = Status;
                LogStatus(_logger, status.StreamConnected, status.Ingest.Tickers, status.Ingest.Trades, status.Ingest.UnknownMarkets, status.Ingest.Combos, status.Ingest.Reconnects, status.Tickers.Buffered + status.Trades.Buffered, status.Tickers.Dropped + status.Trades.Dropped, status.Ingest.GapsPending, status.StreamRestarts, status.DiscoveryFailures, status.Books?.Fresh ?? 0, status.Books?.Watched ?? 0);
            }
        }
    }

    private async Task TryAsync(string what, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogMaintenanceFailed(_logger, what, ex.Message);
        }
    }

    private async Task DisposeWritersAsync()
    {
        await _tickers.DisposeAsync().ConfigureAwait(false);
        await _trades.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Drains the writers, stores the last gaps (including rows the writers abandoned), and marks the run stopped. Never throws.</summary>
    private async Task ShutDownAsync()
    {
        try
        {
            if (_books is not null)
            {
                await _books.StopAsync().ConfigureAwait(false);
            }

            await DisposeWritersAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_options.StopTimeout, _time);
            await _ingestor.FlushAsync(timeout.Token).ConfigureAwait(false);
            await _state.SetAsync(IngestStateKeys.Run, IngestStateKeys.Stopped, _time.GetUtcNow().UtcDateTime, timeout.Token).ConfigureAwait(false);
            LogStopped(_logger);
        }
        catch (Exception ex)
        {
            LogStopFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ingest started with {Markets} markets in the catalog")]
    private static partial void LogStarted(ILogger logger, int markets);

    [LoggerMessage(Level = LogLevel.Information, Message = "ingest stopped cleanly")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "ingest shutdown did not finish cleanly; the run state may still say running")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "an ingest loop failed unexpectedly; stopping")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the stream ended or failed: {Reason}")]
    private static partial void LogStreamFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "opening a new stream (attempt {Attempt}) in {Wait}")]
    private static partial void LogStreamRetry(ILogger logger, int attempt, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Kind} discovery pass failed; the catalog stays as it was until the next tick: {Reason}")]
    private static partial void LogDiscoveryFailed(ILogger logger, PassKind kind, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{What} failed; will retry: {Reason}")]
    private static partial void LogMaintenanceFailed(ILogger logger, string what, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "status: connected={Connected} tickers={Tickers} trades={Trades} unknown_markets={Unknown} combos_skipped={Combos} reconnects={Reconnects} buffered={Buffered} dropped={Dropped} gaps_pending={GapsPending} stream_restarts={Restarts} discovery_failures={DiscoveryFailures} books={BooksFresh}/{BooksWatched}")]
    private static partial void LogStatus(ILogger logger, bool connected, long tickers, long trades, long unknown, long combos, long reconnects, int buffered, long dropped, int gapsPending, long restarts, long discoveryFailures, int booksFresh, int booksWatched);
}
