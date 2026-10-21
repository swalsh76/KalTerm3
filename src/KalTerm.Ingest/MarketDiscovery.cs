using System.Diagnostics;
using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest;

/// <summary>Which listing a discovery pass uses.</summary>
public enum PassKind
{
    /// <summary>Every open non-combo market (about 122 requests at today's size); the only pass that can say a market has left the list.</summary>
    Full,

    /// <summary>Only markets whose metadata changed since the last pass (a few requests): new markets, status changes, closes and settlements.</summary>
    Incremental,
}

/// <summary>What one discovery pass found and did.</summary>
/// <param name="Kind">Which pass this was. An incremental request becomes <see cref="PassKind.Full"/> when no pass has completed yet.</param>
/// <param name="Seen">Non-combo markets listed that were stored (a full pass: all of them; an incremental pass: the known or newly active ones).</param>
/// <param name="SkippedCombos">Combo markets that came back anyway and were skipped (the requests already exclude them).</param>
/// <param name="Ignored">Incremental only: changed markets we do not hold and that are not tradable (settled history, markets not yet open), not stored.</param>
/// <param name="Matched">Markets that already existed and were updated.</param>
/// <param name="Added">Tickers seen for the first time.</param>
/// <param name="Removed">Tickers that are no longer tradable: after a full pass those no longer listed (now <c>unlisted</c>); after an incremental pass known markets that came back
/// in a non-tradable status. An overlapping incremental window can report the same ticker twice, so treat it as idempotent.</param>
/// <param name="Duration">How long the pass took.</param>
public sealed record DiscoveryResult(
    PassKind Kind,
    int Seen,
    int SkippedCombos,
    int Ignored,
    int Matched,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    TimeSpan Duration);

/// <summary>Maps Kalshi's market model onto the stored catalog record.</summary>
public static class MarketMapper
{
    /// <summary>Converts a market; times become UTC and prices become dollars at 10^-6.</summary>
    /// <param name="market">The market as Kalshi returned it.</param>
    /// <returns>The record to store.</returns>
    public static MarketRecord ToRecord(Market market)
    {
        ArgumentNullException.ThrowIfNull(market);
        return new MarketRecord(
            market.Ticker,
            market.EventTicker,
            market.MarketType,
            market.Status,
            market.YesSubTitle,
            market.NoSubTitle,
            market.OpenTime?.UtcDateTime,
            market.CloseTime?.UtcDateTime,
            market.LatestExpirationTime?.UtcDateTime,
            market.SettlementTs?.UtcDateTime,
            market.Result,
            market.SettlementValueDollars?.Micros,
            market.Title);
    }
}

/// <summary>
/// Keeps the <c>market</c> table in step with Kalshi. A <b>full</b> pass lists the open, non-combo markets, upserts them a page at a time and,
/// after a complete non-empty listing only, marks tradable markets that are no longer listed as <c>unlisted</c>. An <b>incremental</b> pass asks only
/// for markets changed since the last completed pass (less a safety overlap), stores the ones we already know, with whatever status they now have,
/// plus any unknown market that is tradable, and never unlists anything. A failed pass changes no bookmark, so the next one covers the same ground.
/// When passes run is the host's business (<see cref="RefreshSchedule"/>, slice 3.4).
/// </summary>
public sealed partial class MarketDiscovery
{
    private readonly IMarketStore _store;
    private readonly MarketIdMap _ids;
    private readonly Func<CancellationToken, IAsyncEnumerable<Market>> _listOpenMarkets;
    private readonly Func<DateTimeOffset, CancellationToken, IAsyncEnumerable<Market>>? _listUpdatedSince;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _overlap;
    private DateTime? _bookmark;

    /// <summary>Creates the discovery.</summary>
    /// <param name="store">The catalog storage.</param>
    /// <param name="ids">Receives the id of every market stored, and tells an incremental pass which markets are known.</param>
    /// <param name="listOpenMarkets">Lists the open, non-combo markets (normally <c>KalshiRestClient.IterateOpenMarketsAsync</c>).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives one line per pass.</param>
    /// <param name="batchSize">How many markets go to the database at once.</param>
    /// <param name="listUpdatedSince">Lists non-combo markets changed since a moment (normally <c>KalshiRestClient.IterateMarketsUpdatedSinceAsync</c>); without it only full passes run.</param>
    /// <param name="overlap">The incremental look-back margin; <see cref="DefaultOverlap"/> when <see langword="null"/>.</param>
    public MarketDiscovery(
        IMarketStore store,
        MarketIdMap ids,
        Func<CancellationToken, IAsyncEnumerable<Market>> listOpenMarkets,
        TimeProvider? time = null,
        ILogger? logger = null,
        int batchSize = 1000,
        Func<DateTimeOffset, CancellationToken, IAsyncEnumerable<Market>>? listUpdatedSince = null,
        TimeSpan? overlap = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(listOpenMarkets);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _store = store;
        _ids = ids;
        _listOpenMarkets = listOpenMarkets;
        _listUpdatedSince = listUpdatedSince;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _batchSize = batchSize;
        _overlap = overlap ?? DefaultOverlap;
        ArgumentOutOfRangeException.ThrowIfLessThan(_overlap, TimeSpan.Zero);
    }

    /// <summary>How far before the last completed pass an incremental listing looks back, to absorb clock differences and in-flight changes.</summary>
    public static TimeSpan DefaultOverlap { get; } = TimeSpan.FromMinutes(2);

    /// <summary>The start time of the last completed pass, or <see langword="null"/> before the first one.</summary>
    public DateTime? Bookmark => _bookmark;

    /// <summary>Runs one full pass.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass found.</returns>
    public Task<DiscoveryResult> RunAsync(CancellationToken cancellationToken = default) => RunAsync(PassKind.Full, cancellationToken);

    /// <summary>Runs one pass of the given kind.</summary>
    /// <param name="kind">Full, or incremental (which runs as full while no pass has completed yet).</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass found.</returns>
    /// <exception cref="InvalidOperationException">An incremental pass was asked for but no incremental listing was supplied.</exception>
    public async Task<DiscoveryResult> RunAsync(PassKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == PassKind.Incremental)
        {
            if (_listUpdatedSince is null)
            {
                throw new InvalidOperationException("No incremental listing was supplied.");
            }

            if (_bookmark is not null)
            {
                return await RunIncrementalAsync(_listUpdatedSince, cancellationToken).ConfigureAwait(false);
            }
        }

        return await RunFullAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DiscoveryResult> RunFullAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var passStart = PassStart();
        var batch = new List<MarketRecord>(_batchSize);
        var added = new List<string>();
        var seen = 0;
        var skipped = 0;
        var matched = 0;

        await foreach (var market in _listOpenMarkets(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (market.IsMultivariate)
            {
                skipped++;
                continue;
            }

            seen++;
            batch.Add(MarketMapper.ToRecord(market));
            if (batch.Count >= _batchSize)
            {
                matched += await FlushAsync(batch, passStart, added, cancellationToken).ConfigureAwait(false);
            }
        }

        matched += await FlushAsync(batch, passStart, added, cancellationToken).ConfigureAwait(false);

        // Only a listing that ran to the end, and found something, may say what is missing: an empty answer is more likely a glitch than
        // an exchange with no open markets, and acting on it would unlist everything.
        IReadOnlyList<string> removed = [];
        if (seen > 0)
        {
            removed = await _store.MarkUnlistedAsync(passStart, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LogEmpty(_logger);
        }

        _bookmark = passStart;
        var result = new DiscoveryResult(PassKind.Full, seen, skipped, 0, matched, added, removed, Stopwatch.GetElapsedTime(started));
        LogPass(_logger, result.Kind, result.Seen, result.Added.Count, result.Removed.Count, result.SkippedCombos, result.Duration.TotalSeconds);
        return result;
    }

    private async Task<DiscoveryResult> RunIncrementalAsync(Func<DateTimeOffset, CancellationToken, IAsyncEnumerable<Market>> list, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var passStart = PassStart();
        var since = new DateTimeOffset(_bookmark!.Value - _overlap, TimeSpan.Zero);
        var batch = new List<MarketRecord>(_batchSize);
        var added = new List<string>();
        var removed = new List<string>();
        var stored = 0;
        var skipped = 0;
        var ignored = 0;
        var matched = 0;

        await foreach (var market in list(since, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (market.IsMultivariate)
            {
                skipped++;
                continue;
            }

            var tradable = MarketStore.TradableStatuses.Contains(market.Status);
            if (!_ids.TryGetId(market.Ticker, out _) && !tradable)
            {
                ignored++;   // history we never held, or a market that has not opened yet: it shows up here again when it becomes active
                continue;
            }

            if (!tradable)
            {
                removed.Add(market.Ticker);
            }

            stored++;
            batch.Add(MarketMapper.ToRecord(market));
            if (batch.Count >= _batchSize)
            {
                matched += await FlushAsync(batch, passStart, added, cancellationToken).ConfigureAwait(false);
            }
        }

        matched += await FlushAsync(batch, passStart, added, cancellationToken).ConfigureAwait(false);

        _bookmark = passStart;
        var result = new DiscoveryResult(PassKind.Incremental, stored, skipped, ignored, matched, added, removed, Stopwatch.GetElapsedTime(started));
        LogPass(_logger, result.Kind, result.Seen, result.Added.Count, result.Removed.Count, result.SkippedCombos, result.Duration.TotalSeconds);
        return result;
    }

    private DateTime PassStart()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);   // the column holds milliseconds
    }

    private async Task<int> FlushAsync(List<MarketRecord> batch, DateTime passStart, List<string> added, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        var result = await _store.UpsertAsync(batch, passStart, cancellationToken).ConfigureAwait(false);
        _ids.AddRange(result.Ids);
        added.AddRange(result.Inserted);
        batch.Clear();
        return result.Matched;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Market discovery listed no open markets; nothing was marked unlisted")]
    private static partial void LogEmpty(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Market discovery ({Kind}): {Seen} stored, {Added} new, {Removed} no longer tradable, {Skipped} combos skipped, {Seconds:F1} s")]
    private static partial void LogPass(ILogger logger, PassKind kind, int seen, int added, int removed, int skipped, double seconds);
}
