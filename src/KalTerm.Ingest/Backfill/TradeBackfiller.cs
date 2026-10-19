using System.Globalization;
using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Backfill;

/// <summary>Reads executed trades from Kalshi for a time range.</summary>
public interface ITradeSource
{
    /// <summary>Every trade, of every market, with a time from <paramref name="from"/> to <paramref name="until"/> (whole seconds, both ends inclusive).</summary>
    /// <param name="from">The first second.</param>
    /// <param name="until">The last second.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The trades, in the order Kalshi returns them.</returns>
    IAsyncEnumerable<Trade> TradesAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default);
}

/// <summary><see cref="ITradeSource"/> over <c>GET /markets/trades</c>.</summary>
/// <param name="client">The REST client (shared with discovery, so one rate limiter covers both).</param>
public sealed class RestTradeSource(KalshiRestClient client) : ITradeSource
{
    private const int PageSize = 1000;

    /// <inheritdoc/>
    public IAsyncEnumerable<Trade> TradesAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default) =>
        client.IterateTradesAsync(KalshiQuery.Empty.With("min_ts", from).With("max_ts", until).With("limit", PageSize), cancellationToken);
}

/// <summary>Tuning for <see cref="TradeBackfiller"/>.</summary>
public sealed record BackfillOptions
{
    /// <summary>Time added before and after a gap, to catch trades near its edges. Default 10 seconds.</summary>
    public TimeSpan Padding { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest window recovered; for a longer gap only the most recent part is. Default 6 hours.</summary>
    public TimeSpan MaxWindow { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The size of the pieces a window is recovered in; it bounds the memory of the duplicate check. Default 15 minutes.</summary>
    public TimeSpan Slice { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a gap must have been closed before it is recovered, so the live rows around its end are stored. Default 30 seconds.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a gap marked running is believed to be in progress before another attempt takes it over. Default 10 minutes.</summary>
    public TimeSpan StaleRunning { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How many attempts a gap gets. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The pause after every 1,000 trades read, so a long recovery leaves the rate limit to discovery. Default 100 milliseconds.</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>A stored trade's time can differ from the second REST reports; the duplicate check looks this far beyond a slice. Default 5 seconds.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate()
    {
        if (Padding < TimeSpan.Zero || Settle < TimeSpan.Zero || PageDelay < TimeSpan.Zero || Tolerance < TimeSpan.Zero
            || MaxWindow < TimeSpan.FromMinutes(1) || Slice < TimeSpan.FromMinutes(1) || StaleRunning <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BackfillOptions), "a window and a slice must be at least a minute and no time may be negative");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
    }
}

/// <summary>What one run of the backfill did.</summary>
/// <param name="GapId">The gap.</param>
/// <param name="Status">How it ended: <see cref="BackfillStatus.Done"/> or the status it was returned to.</param>
/// <param name="Found">Trades REST returned.</param>
/// <param name="Added">Trades written.</param>
/// <param name="Note">What happened.</param>
public sealed record BackfillOutcome(long GapId, string Status, int Found, int Added, string Note);

/// <summary>Recovers the trades of one gap at a time.</summary>
public interface ITradeBackfiller
{
    /// <summary>Takes the oldest gap that is waiting, if any, and recovers its trades. Never throws except when cancelled: a failure is recorded on the gap.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What it did, or <see langword="null"/> when no gap was waiting.</returns>
    Task<BackfillOutcome?> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Recovers the trades Kalshi executed while the stream was not being read. For one closed gap it reads REST over the gap padded at both ends (at most
/// <see cref="BackfillOptions.MaxWindow"/>, the most recent part), a slice at a time, and writes every trade of a catalog market that is not stored yet through the normal
/// trade writer. A trade is "stored" when its market and trade id are in the database for the slice (plus a tolerance), so running a window again can never duplicate a row.
/// Combo trades are skipped as the stream skips them. Ticker updates and order books are not recoverable and are left alone.
/// </summary>
public sealed partial class TradeBackfiller : ITradeBackfiller
{
    private readonly IBackfillStore _store;
    private readonly ITradeSource _source;
    private readonly IRowWriter<TradeRow> _trades;
    private readonly MarketIdMap _ids;
    private readonly BackfillOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates a backfiller.</summary>
    /// <param name="store">The database side.</param>
    /// <param name="source">Reads trades from Kalshi.</param>
    /// <param name="trades">Writes trade rows (the same writer the stream uses).</param>
    /// <param name="ids">The ticker-to-id map.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public TradeBackfiller(IBackfillStore store, ITradeSource source, IRowWriter<TradeRow> trades, MarketIdMap ids, BackfillOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(ids);
        _options = options ?? new BackfillOptions();
        _options.Validate();
        _store = store;
        _source = source;
        _trades = trades;
        _ids = ids;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<BackfillOutcome?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        ClaimedGap? gap;
        try
        {
            gap = await _store.ClaimNextAsync(now, _options.Settle, _options.StaleRunning, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogClaimFailed(_logger, ex.Message);
            return null;
        }

        if (gap is null)
        {
            return null;
        }

        try
        {
            var outcome = await RecoverAsync(gap, cancellationToken).ConfigureAwait(false);
            await _store.CompleteAsync(gap.Id, outcome.Status, outcome.Found, outcome.Added, outcome.Note, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            LogDone(_logger, gap.Id, gap.Kind, outcome.Found, outcome.Added, outcome.Note);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var final = gap.Attempts >= _options.MaxAttempts;
            LogFailed(_logger, gap.Id, gap.Attempts, _options.MaxAttempts, ex.Message);
            await RecordFailureAsync(gap, ex.Message, cancellationToken).ConfigureAwait(false);
            return new BackfillOutcome(gap.Id, final ? BackfillStatus.Failed : BackfillStatus.Pending, 0, 0, ex.Message);
        }
    }

    private async Task RecordFailureAsync(ClaimedGap gap, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _store.FailAsync(gap.Id, _options.MaxAttempts, message, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogClaimFailed(_logger, "could not record the failure of gap " + gap.Id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
        }
    }

    private async Task<BackfillOutcome> RecoverAsync(ClaimedGap gap, CancellationToken cancellationToken)
    {
        var from = WholeSecond(gap.StartedAt - _options.Padding);
        var to = WholeSecond(gap.EndedAt + _options.Padding);
        var truncated = false;
        if (to - from > _options.MaxWindow)
        {
            from = to - _options.MaxWindow;
            truncated = true;
        }

        int found = 0, added = 0, duplicates = 0, combos = 0, unknown = 0;
        var seen = new HashSet<StoredTradeKey>();
        var previous = new HashSet<StoredTradeKey>();
        for (var start = from; start < to; start += _options.Slice)
        {
            var end = start + _options.Slice;
            var last = end >= to;
            if (last)
            {
                end = to;
            }

            var stored = await _store.StoredTradesAsync(start - _options.Tolerance, end + _options.Tolerance, cancellationToken).ConfigureAwait(false);
            var sinceDelay = 0;
            await foreach (var trade in _source.TradesAsync(new DateTimeOffset(start), new DateTimeOffset(last ? end : end.AddSeconds(-1)), cancellationToken).ConfigureAwait(false))
            {
                found++;
                if (++sinceDelay >= 1000)
                {
                    sinceDelay = 0;
                    await Task.Delay(_options.PageDelay, _time, cancellationToken).ConfigureAwait(false);
                }

                if (trade.Ticker.StartsWith(StreamIngestor.ComboPrefix, StringComparison.Ordinal))
                {
                    combos++;
                    continue;
                }

                if (!_ids.TryGetId(trade.Ticker, out var marketId))
                {
                    unknown++;
                    continue;
                }

                var key = new StoredTradeKey(marketId, trade.TradeId);
                if (stored.Contains(key) || previous.Contains(key) || !seen.Add(key))
                {
                    duplicates++;
                    continue;
                }

                if (!_trades.TryWrite(StreamMapper.ToRow(trade, marketId)))
                {
                    throw new InvalidOperationException("the trade writer is full; the gap will be retried");
                }

                added++;
            }

            (previous, seen) = (seen, previous);
            seen.Clear();
        }

        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"{from:yyyy-MM-dd HH:mm:ss} to {to:yyyy-MM-dd HH:mm:ss}; {duplicates} already stored, {combos} combo, {unknown} unknown market{(truncated ? "; gap longer than the window, only its most recent part was recovered" : string.Empty)}");
        return new BackfillOutcome(gap.Id, BackfillStatus.Done, found, added, note);
    }

    private static DateTime WholeSecond(DateTime moment) => new(moment.Ticks - (moment.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    [LoggerMessage(Level = LogLevel.Information, Message = "trade backfill of gap {GapId} ({Kind}): {Found} trades found, {Added} written; {Note}")]
    private static partial void LogDone(ILogger logger, long gapId, string kind, int found, int added, string note);

    [LoggerMessage(Level = LogLevel.Warning, Message = "trade backfill of gap {GapId} failed (attempt {Attempt} of {Max}): {Reason}")]
    private static partial void LogFailed(ILogger logger, long gapId, int attempt, int max, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "trade backfill could not read the gap log: {Reason}")]
    private static partial void LogClaimFailed(ILogger logger, string reason);
}
