using KalTerm.Data.Candles;
using KalTerm.Data.Markets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Candles;

/// <summary>Builds the stored candles as time passes.</summary>
public interface ICandleJob
{
    /// <summary>Builds the buckets that have closed since the last run and rebuilds the ones a finished trade backfill changed. Never throws except when cancelled.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many candles were written.</returns>
    Task<int> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Tuning for <see cref="CandleJob"/>.</summary>
public sealed record CandleJobOptions
{
    /// <summary>How long after a bucket closes it is built, so the last buffered rows are stored first. Default 90 seconds.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The most one run builds of an interval of an hour or more. Default 1 day.</summary>
    public TimeSpan LongSpan { get; init; } = TimeSpan.FromDays(1);

    /// <summary>The most one run builds of a shorter interval. Default 6 hours.</summary>
    public TimeSpan ShortSpan { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Time added around a gap when its candles are rebuilt, matching the trade backfill's padding. Default 10 seconds.</summary>
    public TimeSpan GapPadding { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Throws if a value cannot work.</summary>
    public void Validate()
    {
        if (Settle < TimeSpan.Zero || GapPadding < TimeSpan.Zero || LongSpan < TimeSpan.FromHours(1) || ShortSpan < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CandleJobOptions), "spans must be at least a minute and no time may be negative");
        }
    }
}

/// <summary>
/// Keeps the stored candles current. For each stored interval it remembers how far it has built; each run builds the buckets that have closed since (taking up to a span at a time, so a
/// long catch-up does not hold the database for hours), starting from the oldest raw data on the first run. A bucket is only built from raw rows that still exist: the storage governor drops
/// whole old days, and rebuilding a candle after its rows are gone would erase it, so nothing before the oldest raw day is ever rebuilt. When a gap's trade backfill finishes, the candles over the
/// gap are rebuilt so the trade-derived flag clears.
/// </summary>
public sealed partial class CandleJob : ICandleJob
{
    private readonly ICandleBuilder _builder;
    private readonly ICandleJobStore _store;
    private readonly IReadOnlyList<CandleRule> _rules;
    private readonly Func<IReadOnlyCollection<int>> _watched;
    private readonly CandleJobOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private bool _rulesSaved;

    /// <summary>Creates the job.</summary>
    /// <param name="builder">Builds candles.</param>
    /// <param name="store">Remembers progress.</param>
    /// <param name="rules">The stored intervals.</param>
    /// <param name="watched">The ids of the watch-list markets (for the <see cref="CandleScope.Watched"/> rules).</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public CandleJob(ICandleBuilder builder, ICandleJobStore store, IReadOnlyList<CandleRule> rules, Func<IReadOnlyCollection<int>> watched, CandleJobOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(watched);
        _options = options ?? new CandleJobOptions();
        _options.Validate();
        _builder = builder;
        _store = store;
        _rules = rules;
        _watched = watched;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The ids of the markets named by a watch list that the catalog knows.</summary>
    /// <param name="tickers">The watch list.</param>
    /// <param name="ids">The ticker-to-id map.</param>
    /// <returns>The ids.</returns>
    public static Func<IReadOnlyCollection<int>> WatchedIds(IEnumerable<string> tickers, MarketIdMap ids)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        ArgumentNullException.ThrowIfNull(ids);
        var list = tickers.ToList();
        return () => [.. list.Select(t => ids.TryGetId(t, out var id) ? (int?)id : null).OfType<int>()];
    }

    /// <inheritdoc/>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var written = 0;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (!_rulesSaved)
            {
                await _store.SaveRulesAsync(_rules, now, cancellationToken).ConfigureAwait(false);
                _rulesSaved = true;
            }

            var rawFrom = await _store.RawFromAsync(cancellationToken).ConfigureAwait(false);
            foreach (var rule in _rules)
            {
                written += await BuildNewAsync(rule, now, rawFrom, cancellationToken).ConfigureAwait(false);
            }

            written += await RebuildGapsAsync(now, rawFrom, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, ex.Message);
        }

        return written;
    }

    private async Task<int> BuildNewAsync(CandleRule rule, DateTime now, DateTime? rawFrom, CancellationToken cancellationToken)
    {
        var interval = rule.IntervalSeconds;
        var closed = CandleRules.BucketStart(now - _options.Settle, interval);
        var through = await _store.GetThroughAsync(interval, cancellationToken).ConfigureAwait(false);
        var from = through ?? (rawFrom is { } raw ? CandleRules.BucketStart(raw, interval) : closed);
        if (rawFrom is { } oldest && from < oldest)
        {
            from = CandleRules.BucketStart(oldest.AddSeconds(interval - 1), interval);   // never reach for rows the retention has dropped
        }

        if (from >= closed)
        {
            return 0;
        }

        var span = interval >= 3600 ? _options.LongSpan : _options.ShortSpan;
        var until = from + span < closed ? CandleRules.BucketStart(from + span, interval) : closed;
        if (until <= from)
        {
            until = from.AddSeconds(interval);
        }

        var ids = Ids(rule);
        var count = ids is { Count: 0 } ? 0 : await _builder.BuildAsync(interval, from, until, ids, now, cancellationToken).ConfigureAwait(false);
        await _store.SetThroughAsync(interval, until, now, cancellationToken).ConfigureAwait(false);
        LogBuilt(_logger, rule, from, until, count);
        return count;
    }

    private async Task<int> RebuildGapsAsync(DateTime now, DateTime? rawFrom, CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var gap in await _store.GapsToRebuildAsync(now - _options.Settle, cancellationToken).ConfigureAwait(false))
        {
            foreach (var rule in _rules)
            {
                var interval = rule.IntervalSeconds;
                var through = await _store.GetThroughAsync(interval, cancellationToken).ConfigureAwait(false);
                var from = CandleRules.BucketStart(gap.StartedAt - _options.GapPadding, interval);
                var until = CandleRules.BucketStart(gap.EndedAt + _options.GapPadding, interval).AddSeconds(interval);
                if (rawFrom is { } oldest && from < oldest)
                {
                    from = CandleRules.BucketStart(oldest.AddSeconds(interval - 1), interval);
                }

                if (through is { } built && until > built)
                {
                    until = built;   // buckets not built yet will be built with the gap's final state
                }

                if (through is null || from >= until)
                {
                    continue;
                }

                var ids = Ids(rule);
                written += ids is { Count: 0 } ? 0 : await _builder.BuildAsync(interval, from, until, ids, now, cancellationToken).ConfigureAwait(false);
            }

            await _store.MarkRebuiltAsync(gap.Id, now, cancellationToken).ConfigureAwait(false);
            LogRebuilt(_logger, gap.Id);
        }

        return written;
    }

    private IReadOnlyCollection<int>? Ids(CandleRule rule) => rule.Scope == CandleScope.All ? null : _watched();

    [LoggerMessage(Level = LogLevel.Information, Message = "candles {Rule}: built {From:u} to {Until:u}, {Count} written")]
    private static partial void LogBuilt(ILogger logger, CandleRule rule, DateTime from, DateTime until, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "candles over gap {GapId} rebuilt after its trade backfill")]
    private static partial void LogRebuilt(ILogger logger, long gapId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the candle job failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
