using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace KalTerm.Data.Writing;

/// <summary>
/// A bounded in-memory buffer in front of a database table. Producers call <see cref="TryWrite"/> (never blocks); one background loop
/// writes batches of up to <see cref="BulkWriterOptions.FlushRows"/> rows, at most <see cref="BulkWriterOptions.FlushInterval"/> after
/// the first row of the batch arrived. Retryable failures are retried forever with capped backoff while the buffer absorbs the stall;
/// a batch the database refuses outright is dropped, counted and logged; a full buffer drops new rows, counted, with their time range
/// kept for the gap log (<see cref="TakeDroppedRange"/>).
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed partial class BulkWriter<T> : IRowWriter<T>, IAsyncDisposable
{
    private readonly IBatchSink<T> _sink;
    private readonly IRowMapper<T> _mapper;
    private readonly BulkWriterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _abandon = new();
    private readonly Lock _stats = new();
    private Task? _loop;
    private int _buffered;
    private long _written;
    private long _dropped;
    private long _retries;
    private long _poison;
    private int _consecutiveFailures;
    private DateTimeOffset? _lastSuccess;
    private string? _lastError;
    private TimeSpan _lastWrite;
    private TimeSpan _maxWrite;
    private DateTime? _droppedFrom;
    private DateTime? _droppedTo;
    private long _droppedRangeRows;

    /// <summary>Creates a writer; call <see cref="Start"/> to begin writing.</summary>
    /// <param name="sink">Writes a batch.</param>
    /// <param name="mapper">Describes the rows (their timestamps are used for dropped ranges).</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock used for the flush interval and backoff.</param>
    /// <param name="logger">Receives warnings and errors.</param>
    public BulkWriter(IBatchSink<T> sink, IRowMapper<T> mapper, BulkWriterOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(mapper);
        _sink = sink;
        _mapper = mapper;
        _options = options ?? new BulkWriterOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
    }

    /// <summary>A snapshot of the counters.</summary>
    public BulkWriterStats Stats
    {
        get
        {
            lock (_stats)
            {
                return new BulkWriterStats(
                    Volatile.Read(ref _buffered),
                    _written,
                    _dropped,
                    _retries,
                    _poison,
                    _consecutiveFailures,
                    _consecutiveFailures < _options.UnhealthyAfterFailures,
                    _lastSuccess,
                    _lastError,
                    _lastWrite,
                    _maxWrite);
            }
        }
    }

    /// <summary>Starts the background loop. Call once.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The writer is already started.");
        }

        _loop = Task.Run(RunAsync);
    }

    /// <summary>Queues a row. Never blocks.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="false"/> when the buffer is full (or the writer is stopped) and the row was dropped and counted.</returns>
    public bool TryWrite(T row)
    {
        if (Interlocked.Increment(ref _buffered) > _options.MaxBufferedRows || !_channel.Writer.TryWrite(row))
        {
            Interlocked.Decrement(ref _buffered);
            RecordDropped(_mapper.Timestamp(row), 1);
            return false;
        }

        return true;
    }

    /// <summary>Returns the time range covered by rows dropped since the last call, and resets it.</summary>
    /// <returns>The range, or <see langword="null"/> when nothing was dropped.</returns>
    public DroppedRange? TakeDroppedRange()
    {
        lock (_stats)
        {
            if (_droppedFrom is null || _droppedTo is null)
            {
                return null;
            }

            var range = new DroppedRange(_droppedFrom.Value, _droppedTo.Value, _droppedRangeRows);
            _droppedFrom = null;
            _droppedTo = null;
            _droppedRangeRows = 0;
            return range;
        }
    }

    /// <summary>Stops accepting rows, writes what is buffered for at most <see cref="BulkWriterOptions.ShutdownTimeout"/>, and drops (counts) the rest. A sink that is <see cref="IDisposable"/> is disposed too.</summary>
    /// <returns>A task that completes when the loop has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await _abandon.CancelAsync().ConfigureAwait(false);
                await _loop.ConfigureAwait(false);
            }
        }

        _stop.Dispose();
        _abandon.Dispose();
        (_sink as IDisposable)?.Dispose();
    }

    private async Task RunAsync()
    {
        var batch = new List<T>(_options.FlushRows);
        try
        {
            while (true)
            {
                batch.Clear();
                if (!await CollectAsync(batch).ConfigureAwait(false))
                {
                    return;
                }

                await WriteWithRetryAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // abandoned at shutdown: the batch in hand and anything still buffered are dropped below
            DropRemaining(batch);
        }
    }

    /// <summary>Fills <paramref name="batch"/>; false when the writer is stopped and the buffer is empty.</summary>
    private async Task<bool> CollectAsync(List<T> batch)
    {
        var reader = _channel.Reader;

        // The first row: wait for it, however long. After a stop request the channel is completed, so this ends once drained.
        if (!await reader.WaitToReadAsync(_abandon.Token).ConfigureAwait(false))
        {
            return false;
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(_abandon.Token);
        var deadline = Task.Delay(_options.FlushInterval, _time, window.Token);
        try
        {
            while (batch.Count < _options.FlushRows)
            {
                while (batch.Count < _options.FlushRows && reader.TryRead(out var row))
                {
                    batch.Add(row);
                    Interlocked.Decrement(ref _buffered);
                }

                if (batch.Count >= _options.FlushRows || _stop.IsCancellationRequested)
                {
                    break; // full, or shutting down: write what we have now
                }

                var more = reader.WaitToReadAsync(window.Token).AsTask();
                if (await Task.WhenAny(more, deadline).ConfigureAwait(false) == deadline || !await more.ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            await window.CancelAsync().ConfigureAwait(false);
        }

        return true;
    }

    private async Task WriteWithRetryAsync(List<T> batch)
    {
        var backoff = _options.InitialBackoff;
        while (true)
        {
            try
            {
                var started = _time.GetTimestamp();
                await _sink.WriteAsync(batch, _abandon.Token).ConfigureAwait(false);
                var took = _time.GetElapsedTime(started);
                lock (_stats)
                {
                    _lastWrite = took;
                    _maxWrite = took > _maxWrite ? took : _maxWrite;
                    _written += batch.Count;
                    _consecutiveFailures = 0;
                    _lastSuccess = _time.GetUtcNow();
                }

                return;
            }
            catch (OperationCanceledException) when (_abandon.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var retry = SqlRetryPolicy.IsRetryable(ex);
                lock (_stats)
                {
                    _lastError = ex.Message;
                    if (retry)
                    {
                        _consecutiveFailures++;
                        _retries++;
                    }
                    else
                    {
                        _poison++;
                    }
                }

                if (!retry)
                {
                    LogPoison(_logger, ex, batch.Count);
                    DropBatch(batch);
                    return;
                }

                LogRetry(_logger, ex.Message, backoff);
                var jitter = 1 + (Random.Shared.NextDouble() * _options.JitterFraction);
                await Task.Delay(backoff * jitter, _time, _abandon.Token).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(_options.MaxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }

    private void DropBatch(List<T> batch)
    {
        foreach (var row in batch)
        {
            RecordDropped(_mapper.Timestamp(row), 1);
        }
    }

    private void DropRemaining(List<T> batch)
    {
        DropBatch(batch);
        var left = batch.Count;
        while (_channel.Reader.TryRead(out var row))
        {
            RecordDropped(_mapper.Timestamp(row), 1);
            Interlocked.Decrement(ref _buffered);
            left++;
        }

        if (left > 0)
        {
            LogShutdownDropped(_logger, left);
        }
    }

    private void RecordDropped(DateTime ts, long rows)
    {
        lock (_stats)
        {
            _dropped += rows;
            _droppedRangeRows += rows;
            _droppedFrom = _droppedFrom is null || ts < _droppedFrom ? ts : _droppedFrom;
            _droppedTo = _droppedTo is null || ts > _droppedTo ? ts : _droppedTo;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database write failed ({Reason}); retrying in {Delay}")]
    private static partial void LogRetry(ILogger logger, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database refused a batch of {Rows} rows for a reason a retry cannot fix; the batch is dropped")]
    private static partial void LogPoison(ILogger logger, Exception exception, int rows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shutdown: {Rows} buffered rows were not written and are dropped")]
    private static partial void LogShutdownDropped(ILogger logger, int rows);
}
