using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.Writing;
using KalTerm.Kalshi;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest;

/// <summary>Tuning for <see cref="StreamIngestor"/>.</summary>
public sealed record StreamIngestorOptions
{
    /// <summary>How often dropped-row ranges are collected from the writers and gaps are stored. Default 1 second.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The most gaps held while the database is unavailable; beyond it the oldest are discarded (and counted). Default 1,000.</summary>
    public int MaxPendingGaps { get; init; } = 1_000;

    /// <summary>The longest the final flush waits when the stream ends. Default 10 seconds.</summary>
    public TimeSpan FinalFlushTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A snapshot of a <see cref="StreamIngestor"/>.</summary>
/// <param name="Tickers">Ticker rows handed to the writer.</param>
/// <param name="Trades">Trade rows handed to the writer.</param>
/// <param name="UnknownMarkets">Data messages for a market not in the catalog yet (not stored); combos are counted separately.</param>
/// <param name="Combos">Data messages for combo (multivariate) markets, skipped by design (D8).</param>
/// <param name="Malformed">Data messages whose payload could not be read.</param>
/// <param name="Ignored">Messages of other types (including the sequence-number markers).</param>
/// <param name="Reconnects">Times the WebSocket client replaced the link.</param>
/// <param name="GapsLogged">Gap rows stored.</param>
/// <param name="GapsPending">Gap rows waiting for the database.</param>
/// <param name="GapsLost">Gap rows discarded because too many were waiting.</param>
/// <param name="LastDataAt">When the last ticker or trade message arrived.</param>
public sealed record StreamIngestorStats(
    long Tickers,
    long Trades,
    long UnknownMarkets,
    long Combos,
    long Malformed,
    long Ignored,
    long Reconnects,
    long GapsLogged,
    int GapsPending,
    long GapsLost,
    DateTimeOffset? LastDataAt);

/// <summary>
/// Turns the WebSocket message stream into rows for the writers and records every gap in the data: a replaced link, a link lost for good,
/// and rows a writer had to drop. Gaps are kept and retried while the database is unavailable. It can be run again for a later stream: counters,
/// the last data time and any waiting gaps carry over.
/// </summary>
public sealed partial class StreamIngestor
{
    /// <summary>The ticker prefix of combo (multivariate) markets, which are skipped (decision D8).</summary>
    public const string ComboPrefix = "KXMVE";

    private readonly MarketIdMap _ids;
    private readonly IRowWriter<TickerRow> _tickers;
    private readonly IRowWriter<TradeRow> _trades;
    private readonly IGapLog _gaps;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly StreamIngestorOptions _options;
    private readonly List<GapRecord> _pending = [];
    private long _tickerRows;
    private long _tradeRows;
    private long _unknown;
    private long _combos;
    private long _malformed;
    private long _ignored;
    private long _reconnects;
    private long _logged;
    private long _lost;
    private int _pendingCount;
    private DateTimeOffset? _lastData;
    private DateTimeOffset _nextFlush;

    /// <summary>Creates an ingestor.</summary>
    /// <param name="ids">Maps tickers to stored market ids.</param>
    /// <param name="tickers">Receives ticker rows.</param>
    /// <param name="trades">Receives trade rows.</param>
    /// <param name="gaps">Stores the gaps.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives warnings and errors.</param>
    public StreamIngestor(
        MarketIdMap ids,
        IRowWriter<TickerRow> tickers,
        IRowWriter<TradeRow> trades,
        IGapLog gaps,
        StreamIngestorOptions? options = null,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(tickers);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(gaps);
        options ??= new StreamIngestorOptions();
        if (options.FlushInterval <= TimeSpan.Zero || options.MaxPendingGaps < 1 || options.FinalFlushTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FlushInterval, MaxPendingGaps and FinalFlushTimeout must be positive");
        }

        _ids = ids;
        _tickers = tickers;
        _trades = trades;
        _gaps = gaps;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _nextFlush = _time.GetUtcNow() + options.FlushInterval;
    }

    /// <summary>Counters for monitoring (for example a status endpoint).</summary>
    public StreamIngestorStats Stats => new(
        Interlocked.Read(ref _tickerRows),
        Interlocked.Read(ref _tradeRows),
        Interlocked.Read(ref _unknown),
        Interlocked.Read(ref _combos),
        Interlocked.Read(ref _malformed),
        Interlocked.Read(ref _ignored),
        Interlocked.Read(ref _reconnects),
        Interlocked.Read(ref _logged),
        Volatile.Read(ref _pendingCount),
        Interlocked.Read(ref _lost),
        _lastData);

    /// <summary>
    /// Consumes the stream until it ends. When the stream ends with a <see cref="KalshiWsException"/> the loss is recorded as a gap and the
    /// exception is rethrown for the host to handle. Gaps and dropped ranges are flushed one last time however it ends.
    /// </summary>
    /// <param name="messages">The messages, for example <see cref="KalshiWebSocket.Messages"/>.</param>
    /// <param name="cancellationToken">Stops the consumer.</param>
    /// <returns>A task that completes when the stream has ended and the last flush has run.</returns>
    public async Task RunAsync(IAsyncEnumerable<WsMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        try
        {
            await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Handle(message);
                if (_time.GetUtcNow() >= _nextFlush)
                {
                    await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (KalshiWsException ex)
        {
            var now = _time.GetUtcNow();
            AddGap(new GapRecord(GapKinds.WsLost, (_lastData ?? now).UtcDateTime, now.UtcDateTime, ex.Message));
            throw;
        }
        finally
        {
            using var timeout = new CancellationTokenSource(_options.FinalFlushTimeout, _time);
            await FlushCoreAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Records a gap that happened between streams. Call it only while <see cref="RunAsync"/> is not running; the next flush stores it.</summary>
    /// <param name="kind">The gap kind, see <see cref="GapKinds"/>.</param>
    /// <param name="from">When the gap began (UTC).</param>
    /// <param name="to">When it ended (UTC).</param>
    /// <param name="detail">Free text for the operator.</param>
    public void NoteGap(string kind, DateTime from, DateTime to, string detail) => AddGap(new GapRecord(kind, from, to, detail));

    /// <summary>Collects the writers' dropped ranges and stores the waiting gaps; a failed store leaves them for the next flush. Call it only while <see cref="RunAsync"/> is not running.</summary>
    /// <param name="cancellationToken">Cancels the store.</param>
    /// <returns>A task that completes when the flush has been attempted.</returns>
    public Task FlushAsync(CancellationToken cancellationToken = default) => FlushCoreAsync(cancellationToken);

    private static bool IsPowerOfTen(long n)
    {
        while (n >= 10 && n % 10 == 0)
        {
            n /= 10;
        }

        return n == 1;
    }

    private void Handle(WsMessage message)
    {
        var received = message.ReceivedAt ?? _time.GetUtcNow();
        try
        {
            switch (message.Type)
            {
                case "ticker" when message.Payload() is TickerMsg ticker:
                    _lastData = received;
                    if (_ids.TryGetId(ticker.MarketTicker, out var tickerId))
                    {
                        _tickers.TryWrite(StreamMapper.ToRow(ticker, tickerId, received));
                        Interlocked.Increment(ref _tickerRows);
                    }
                    else
                    {
                        NoteUnknown(ticker.MarketTicker);
                    }

                    break;
                case "trade" when message.Payload() is TradeMsg trade:
                    _lastData = received;
                    if (_ids.TryGetId(trade.MarketTicker, out var tradeId))
                    {
                        _trades.TryWrite(StreamMapper.ToRow(trade, tradeId, received));
                        Interlocked.Increment(ref _tradeRows);
                    }
                    else
                    {
                        NoteUnknown(trade.MarketTicker);
                    }

                    break;
                case KalshiWebSocket.ReconnectedType when message.Payload() is ReconnectedMsg reconnected:
                    Interlocked.Increment(ref _reconnects);
                    AddGap(new GapRecord(
                        GapKinds.WsReconnected,
                        (_lastData ?? received).UtcDateTime,
                        received.UtcDateTime,
                        $"{reconnected.Reason}; {reconnected.Dropped} messages dropped; {reconnected.Resubscribed.Count} subscriptions restored, {reconnected.Failed.Count} refused"));
                    break;
                default:
                    Interlocked.Increment(ref _ignored);
                    break;
            }
        }
        catch (KalshiResponseException ex)
        {
            Interlocked.Increment(ref _malformed);
            LogMalformed(_logger, message.Type, ex.Message);
        }
    }

    private void NoteUnknown(string ticker)
    {
        if (ticker.StartsWith(ComboPrefix, StringComparison.Ordinal))
        {
            // Combos are out of scope (D8): the feed carries them for every subscription, so count them quietly.
            Interlocked.Increment(ref _combos);
            return;
        }

        var count = Interlocked.Increment(ref _unknown);
        if (IsPowerOfTen(count))
        {
            LogUnknownMarket(_logger, ticker, count);
        }
    }

    private void AddGap(GapRecord gap)
    {
        if (_pending.Count >= _options.MaxPendingGaps)
        {
            _pending.RemoveAt(0);
            Interlocked.Increment(ref _lost);
            LogGapDiscarded(_logger);
        }

        _pending.Add(gap);
        Volatile.Write(ref _pendingCount, _pending.Count);
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        _nextFlush = _time.GetUtcNow() + _options.FlushInterval;
        AddDropped("ticker", _tickers.TakeDroppedRange());
        AddDropped("trade", _trades.TakeDroppedRange());
        if (_pending.Count == 0)
        {
            return;
        }

        var batch = _pending.ToArray();
        try
        {
            await _gaps.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogGapStoreFailed(_logger, batch.Length, ex.Message);
            return;
        }

        _pending.RemoveRange(0, batch.Length);
        Volatile.Write(ref _pendingCount, _pending.Count);
        Interlocked.Add(ref _logged, batch.Length);
    }

    private void AddDropped(string table, DroppedRange? range)
    {
        if (range is not null)
        {
            AddGap(new GapRecord(GapKinds.WriterDropped, range.From, range.To, $"{table}: {range.Rows} rows dropped by the writer"));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "dropping a malformed {Type} message: {Reason}")]
    private static partial void LogMalformed(ILogger logger, string type, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "data for a market not in the catalog (first: {Ticker}); {Count} so far, not stored")]
    private static partial void LogUnknownMarket(ILogger logger, string ticker, long count);

    [LoggerMessage(Level = LogLevel.Error, Message = "too many gap records waiting for the database; the oldest was discarded")]
    private static partial void LogGapDiscarded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "could not store {Count} gap records; will retry: {Reason}")]
    private static partial void LogGapStoreFailed(ILogger logger, int count, string reason);
}
