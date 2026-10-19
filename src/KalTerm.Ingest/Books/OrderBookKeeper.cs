using System.Net.Sockets;
using KalTerm.Data;
using KalTerm.Data.Books;
using KalTerm.Kalshi;
using KalTerm.Kalshi.Books;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Books;

/// <summary>Fetches a market's order book over REST: the fallback when an in-stream snapshot cannot be had.</summary>
public interface IOrderBookRest
{
    /// <summary>Fetches the full book.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The book.</returns>
    Task<OrderBook> OrderBookAsync(string ticker, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IOrderBookRest"/> over the read-only REST client.</summary>
/// <param name="client">The client; the caller owns it.</param>
public sealed class RestOrderBook(KalshiRestClient client) : IOrderBookRest
{
    /// <inheritdoc/>
    public Task<OrderBook> OrderBookAsync(string ticker, CancellationToken cancellationToken = default) => client.OrderBookAsync(ticker, cancellationToken: cancellationToken);
}

/// <summary>What the stream loop and the runner need from whatever keeps the books.</summary>
public interface IBookKeeper : IBookHandler
{
    /// <summary>A stream was opened and subscribed: begin following its order book subscription.</summary>
    /// <param name="session">The stream.</param>
    void SessionOpened(IStreamSession session);

    /// <summary>The stream ended: recoveries in flight are abandoned.</summary>
    void SessionClosed();

    /// <summary>Writes changed books (and, now and then, all of them) to the database.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the books have been written, or the attempt has failed and will be retried.</returns>
    Task PublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Abandons recoveries in flight and waits for them to end.</summary>
    /// <returns>A task that completes when they have.</returns>
    Task StopAsync();

    /// <summary>Counters for monitoring.</summary>
    BookKeeperStats Stats { get; }
}

/// <summary>Receives the stream's messages that concern order books.</summary>
public interface IBookHandler
{
    /// <summary>Applies one stream message (an order book snapshot or delta, a control marker, a command error, or a reconnect).</summary>
    /// <param name="message">The message.</param>
    void Handle(WsMessage message);
}

/// <summary>Tuning for <see cref="OrderBookKeeper"/>.</summary>
public sealed record OrderBookKeeperOptions
{
    /// <summary>How long to wait for an in-stream snapshot before falling back to REST. Default 5 seconds.</summary>
    public TimeSpan SnapshotTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a book may stay stale, with no recovery running, before another recovery is started. Default 30 seconds.</summary>
    public TimeSpan RetryStaleAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often every book is written even if it did not change, so a quiet book still shows that the ingest is alive. Default 15 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate()
    {
        if (SnapshotTimeout <= TimeSpan.Zero || RetryStaleAfter <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OrderBookKeeperOptions), "every interval must be positive");
        }
    }
}

/// <summary>A snapshot of the keeper's counters.</summary>
/// <param name="Watched">Markets on the watch list.</param>
/// <param name="Fresh">Watched markets with a trustworthy book.</param>
/// <param name="Stale">Watched markets with no book or one that cannot be trusted yet.</param>
/// <param name="Gaps">Gaps found.</param>
/// <param name="Resyncs">Recoveries started.</param>
/// <param name="RestFallbacks">Books rebuilt from REST.</param>
/// <param name="ResyncFailures">Recoveries that failed completely.</param>
/// <param name="Published">Rows written to the database.</param>
public sealed record BookKeeperStats(int Watched, int Fresh, int Stale, long Gaps, long Resyncs, long RestFallbacks, long ResyncFailures, long Published);

/// <summary>
/// Keeps the order books of the watch list: feeds the stream's order book messages to a <see cref="BookTracker"/>, recovers a book after a gap (an in-stream
/// snapshot first, waiting up to the timeout, then the REST book, which is marked approximate), and publishes the current books to the database for the API.
/// Ported from the reference's <c>OrderBookFeed</c>. A book with no data yet or with an unrecovered gap is published as stale. Nothing here writes deltas anywhere.
/// </summary>
public sealed partial class OrderBookKeeper : IBookKeeper, IDisposable
{
    private readonly Lock _lock = new();
    private readonly BookTracker _tracker = new();
    private readonly IReadOnlyList<string> _watch;
    private readonly IOrderBookRest _rest;
    private readonly IBookStateStore _store;
    private readonly Data.Markets.MarketIdMap _ids;
    private readonly OrderBookKeeperOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DateTime> _updatedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _version = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _published = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _staleSince = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _requests = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _fresh = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resyncing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownLogged = new(StringComparer.Ordinal);
    private readonly List<Task> _tasks = [];
    private CancellationTokenSource _recoveries = new();
    private IStreamSession? _session;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private long _gaps;
    private long _resyncs;
    private long _restFallbacks;
    private long _resyncFailures;
    private long _publishedRows;

    /// <summary>Creates the keeper.</summary>
    /// <param name="watchList">The markets to keep books for; at least one.</param>
    /// <param name="rest">The REST fallback.</param>
    /// <param name="store">Where the books are published.</param>
    /// <param name="ids">Maps tickers to stored market ids (the published row is keyed by id).</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives warnings and errors.</param>
    public OrderBookKeeper(
        IEnumerable<string> watchList,
        IOrderBookRest rest,
        IBookStateStore store,
        Data.Markets.MarketIdMap ids,
        OrderBookKeeperOptions? options = null,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(watchList);
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        _watch = [.. watchList.Distinct(StringComparer.Ordinal)];
        if (_watch.Count == 0)
        {
            throw new ArgumentException("the watch list is empty; an order book subscription with no markets would mean every market", nameof(watchList));
        }

        _options = options ?? new OrderBookKeeperOptions();
        _options.Validate();
        _rest = rest;
        _store = store;
        _ids = ids;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The markets whose books are kept.</summary>
    public IReadOnlyList<string> WatchList => _watch;

    /// <inheritdoc/>
    public BookKeeperStats Stats
    {
        get
        {
            lock (_lock)
            {
                var stale = _watch.Count(IsStale);
                return new BookKeeperStats(_watch.Count, _watch.Count - stale, stale, _gaps, _resyncs, _restFallbacks, _resyncFailures, _publishedRows);
            }
        }
    }

    /// <inheritdoc/>
    public void SessionOpened(IStreamSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            _session = session;
            if (_recoveries.IsCancellationRequested)
            {
                _recoveries.Dispose();
                _recoveries = new CancellationTokenSource();
            }

            if (session.OrderBookSid is { } sid)
            {
                _tracker.BeginSubscription(sid, _watch);
                TouchAll(_time.GetUtcNow());
            }
        }
    }

    /// <inheritdoc/>
    public void SessionClosed()
    {
        lock (_lock)
        {
            _session = null;
            _recoveries.Cancel();
            _requests.Clear();
        }
    }

    /// <inheritdoc/>
    public void Handle(WsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            var now = message.ReceivedAt ?? _time.GetUtcNow();
            if (message.Type == "error")
            {
                if (message.Id is { } id && _requests.Remove(id, out var ticker) && _fresh.TryGetValue(ticker, out var waiting))
                {
                    LogSnapshotRejected(_logger, ticker);
                    waiting.TrySetResult(false);
                }

                return;
            }

            IReadOnlyList<BookEvent> events;
            try
            {
                events = _tracker.Process(message);
            }
            catch (KalshiResponseException ex)
            {
                // A book message that cannot be read: nothing about the books can be trusted until they are rebuilt.
                LogUnreadable(_logger, ex.Message);
                _tracker.MarkStale(_watch);
                foreach (var ticker in _watch)
                {
                    MarkStale(ticker, now);
                }

                StartRecoveries(_watch, now);
                return;
            }

            foreach (var e in events)
            {
                OnEvent(e, message, now);
            }
        }
    }

    /// <inheritdoc/>
    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        List<BookState> rows = [];
        List<(string Ticker, long Version)> pending = [];
        var heartbeat = false;
        lock (_lock)
        {
            heartbeat = now - _lastHeartbeat >= _options.HeartbeatInterval;
            foreach (var ticker in _watch)
            {
                if (!_ids.TryGetId(ticker, out var marketId))
                {
                    if (_unknownLogged.Add(ticker))
                    {
                        LogNotInCatalog(_logger, ticker);
                    }

                    continue;
                }

                var version = _version.GetValueOrDefault(ticker);
                if (!heartbeat && _published.TryGetValue(ticker, out var done) && done == version)
                {
                    continue;
                }

                rows.Add(Row(ticker, marketId, now));
                pending.Add((ticker, version));
            }

            RetryOldStaleBooks(now);
        }

        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(rows, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPublishFailed(_logger, ex.Message);
            return;
        }

        lock (_lock)
        {
            foreach (var (ticker, version) in pending)
            {
                _published[ticker] = version;
            }

            if (heartbeat)
            {
                _lastHeartbeat = now;
            }

            _publishedRows += rows.Count;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        Task[] running;
        lock (_lock)
        {
            _recoveries.Cancel();
            running = [.. _tasks];
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Abandoned on purpose.
        }
    }

    /// <summary>Releases the recovery cancellation source; call after <see cref="StopAsync"/>.</summary>
    public void Dispose() => _recoveries.Dispose();

    private bool IsStale(string ticker) => !_tracker.Books.ContainsKey(ticker) || _tracker.Stale.Contains(ticker);

    private BookState Row(string ticker, int marketId, DateTimeOffset now)
    {
        _tracker.Books.TryGetValue(ticker, out var book);
        var stale = IsStale(ticker);
        return new BookState(
            marketId,
            _updatedAt.TryGetValue(ticker, out var updated) ? updated : now.UtcDateTime,
            now.UtcDateTime,
            stale,
            book?.Approximate ?? false,
            Levels(book, "yes"),
            Levels(book, "no"));
    }

    private static List<BookLevel> Levels(LocalOrderBook? book, string side) =>
        book is null ? [] : [.. book.Bids(side).Take(BookLevelText.MaxLevels).Select(l => new BookLevel(l.Price.Micros, l.Quantity.Hundredths))];

    private void OnEvent(BookEvent e, WsMessage message, DateTimeOffset now)
    {
        switch (e.Kind)
        {
            case BookEventKinds.Snapshot:
                Touch(e.Ticker!, now);
                _staleSince.Remove(e.Ticker!);
                if (e.Detail.Length == 0 && _fresh.TryGetValue(e.Ticker!, out var waiting))
                {
                    waiting.TrySetResult(true);
                }

                break;
            case BookEventKinds.Delta:
                Touch(e.Ticker!, now);
                break;
            case BookEventKinds.Gap:
                _gaps++;
                LogGap(_logger, string.Join(",", e.AffectedTickers), e.Detail);
                foreach (var ticker in e.AffectedTickers)
                {
                    MarkStale(ticker, now);
                }

                StartRecoveries(e.AffectedTickers, now);
                break;
            case BookEventKinds.Reset:
                OnReconnected(message, now);
                break;
            default:
                break;
        }
    }

    /// <summary>The connection was replaced: the books are gone, the sequences restart, and the order book subscription is restored under a new id (its snapshots come by themselves).</summary>
    private void OnReconnected(WsMessage message, DateTimeOffset now)
    {
        _recoveries.Cancel();
        _recoveries.Dispose();
        _recoveries = new CancellationTokenSource();
        _requests.Clear();
        _fresh.Clear();
        _resyncing.Clear();
        TouchAll(now);
        ReconnectedMsg? payload = null;
        try
        {
            payload = message.Payload() as ReconnectedMsg;
        }
        catch (KalshiResponseException)
        {
            // Unreadable: treated as "not restored" below.
        }

        var restored = payload?.Resubscribed.FirstOrDefault(r => r.Channel == KalshiStreamSource.OrderBookChannel);
        if (restored is not null)
        {
            _tracker.BeginSubscription(restored.Sid, _watch);
        }
        else
        {
            LogNotRestored(_logger);
        }
    }

    private void StartRecoveries(IEnumerable<string> tickers, DateTimeOffset now)
    {
        foreach (var ticker in tickers)
        {
            if (!_watch.Contains(ticker) || !_resyncing.Add(ticker))
            {
                continue;
            }

            _resyncs++;
            _staleSince.TryAdd(ticker, now);
            var token = _recoveries.Token;
            var task = Task.Run(() => ResyncAsync(ticker, token), CancellationToken.None);
            _tasks.Add(task);
            _ = task.ContinueWith(
                done =>
                {
                    lock (_lock)
                    {
                        _tasks.Remove(done);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Books that stayed stale with no recovery running (a recovery failed, or the subscription was not restored) get another go.</summary>
    private void RetryOldStaleBooks(DateTimeOffset now)
    {
        if (_session is null)
        {
            return;
        }

        var due = _watch.Where(t => IsStale(t) && !_resyncing.Contains(t) && _staleSince.TryGetValue(t, out var since) && now - since >= _options.RetryStaleAfter).ToList();
        foreach (var ticker in due)
        {
            _staleSince[ticker] = now;
        }

        StartRecoveries(due, now);
    }

    private async Task ResyncAsync(string ticker, CancellationToken cancellationToken)
    {
        var recovered = false;
        try
        {
            recovered = await RequestFreshSnapshotAsync(ticker, cancellationToken).ConfigureAwait(false);
            if (!recovered && !cancellationToken.IsCancellationRequested)
            {
                recovered = await RebuildFromRestAsync(ticker, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The stream ended or the keeper stopped: the next session starts from fresh snapshots.
        }
        finally
        {
            lock (_lock)
            {
                _resyncing.Remove(ticker);
                _fresh.Remove(ticker);
                if (!recovered && !cancellationToken.IsCancellationRequested)
                {
                    _resyncFailures++;
                }
            }
        }
    }

    /// <summary>Asks the stream for a fresh snapshot and waits for it; false if it cannot be asked, is rejected, or does not arrive in time.</summary>
    private async Task<bool> RequestFreshSnapshotAsync(string ticker, CancellationToken cancellationToken)
    {
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IStreamSession? session;
        lock (_lock)
        {
            session = _session;
            _fresh[ticker] = waiting;
        }

        if (session?.OrderBookSid is not { } sid)
        {
            return false;
        }

        try
        {
            var id = await session.RequestSnapshotAsync(sid, ticker, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _requests[id] = ticker;
            }

            return await waiting.Task.WaitAsync(_options.SnapshotTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KalshiWsException or TimeoutException)
        {
            LogSnapshotUnavailable(_logger, ticker, ex.Message);
            return false;
        }
    }

    private async Task<bool> RebuildFromRestAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var book = await _rest.OrderBookAsync(ticker, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _tracker.ApplyRestSnapshot(ticker, book);
                Touch(ticker, _time.GetUtcNow());
                _restFallbacks++;
                _staleSince.Remove(ticker);
            }

            LogRestFallback(_logger, ticker);
            return true;
        }
        catch (Exception ex) when (ex is KalshiApiException or KalshiResponseException or HttpRequestException or SocketException or IOException or TimeoutException)
        {
            LogResyncFailed(_logger, ticker, ex.Message);
            return false;
        }
    }

    private void MarkStale(string ticker, DateTimeOffset now)
    {
        _staleSince.TryAdd(ticker, now);
        Touch(ticker, now);
    }

    private void Touch(string ticker, DateTimeOffset now)
    {
        _updatedAt[ticker] = now.UtcDateTime;
        _version[ticker] = _version.GetValueOrDefault(ticker) + 1;
    }

    private void TouchAll(DateTimeOffset now)
    {
        foreach (var ticker in _watch)
        {
            _staleSince.TryAdd(ticker, now);
            Touch(ticker, now);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "order book gap for {Tickers}: {Detail}; the books are stale until recovered")]
    private static partial void LogGap(ILogger logger, string tickers, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "an order book message could not be read ({Reason}); rebuilding every watched book")]
    private static partial void LogUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the order book subscription was not restored after the reconnect; the books stay stale until it is")]
    private static partial void LogNotRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the snapshot request for {Ticker} was rejected")]
    private static partial void LogSnapshotRejected(ILogger logger, string ticker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "no in-stream snapshot for {Ticker} ({Reason}); falling back to REST")]
    private static partial void LogSnapshotUnavailable(ILogger logger, string ticker, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "order book for {Ticker} rebuilt from REST (approximate)")]
    private static partial void LogRestFallback(ILogger logger, string ticker);

    [LoggerMessage(Level = LogLevel.Error, Message = "could not recover the order book for {Ticker}: {Reason}; it stays stale and will be retried")]
    private static partial void LogResyncFailed(ILogger logger, string ticker, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "watched market {Ticker} is not in the catalog; its book cannot be published until discovery has seen it")]
    private static partial void LogNotInCatalog(ILogger logger, string ticker);

    [LoggerMessage(Level = LogLevel.Error, Message = "could not publish the order books; will retry: {Reason}")]
    private static partial void LogPublishFailed(ILogger logger, string reason);
}
