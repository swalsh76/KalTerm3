using System.Diagnostics;
using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest;

/// <summary>What one discovery pass found and did.</summary>
/// <param name="Seen">Open non-combo markets listed.</param>
/// <param name="SkippedCombos">Combo markets that came back anyway and were skipped (the request already excludes them).</param>
/// <param name="Matched">Markets that already existed and were updated.</param>
/// <param name="Added">Tickers seen for the first time.</param>
/// <param name="Removed">Tickers that were tradable before but are no longer listed (now <c>unlisted</c>).</param>
/// <param name="Duration">How long the pass took.</param>
public sealed record DiscoveryResult(int Seen, int SkippedCombos, int Matched, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, TimeSpan Duration);

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
            market.SettlementValueDollars?.Micros);
    }
}

/// <summary>
/// Keeps the <c>market</c> table in step with Kalshi: lists the open, non-combo markets, upserts them a page at a time, and, after a
/// <b>complete</b> listing only, marks tradable markets that are no longer listed as <c>unlisted</c>. A failed listing unlists nothing
/// (the upserts already made are valid and stay). When the schedule runs a pass is the host's business (<see cref="RefreshSchedule"/>, slice 3.4).
/// </summary>
public sealed partial class MarketDiscovery
{
    private readonly IMarketStore _store;
    private readonly MarketIdMap _ids;
    private readonly Func<CancellationToken, IAsyncEnumerable<Market>> _listOpenMarkets;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _batchSize;

    /// <summary>Creates the discovery.</summary>
    /// <param name="store">The catalog storage.</param>
    /// <param name="ids">Receives the id of every market seen.</param>
    /// <param name="listOpenMarkets">Lists the open, non-combo markets (normally <c>KalshiRestClient.IterateOpenMarketsAsync</c>).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives one line per pass.</param>
    /// <param name="batchSize">How many markets go to the database at once.</param>
    public MarketDiscovery(
        IMarketStore store,
        MarketIdMap ids,
        Func<CancellationToken, IAsyncEnumerable<Market>> listOpenMarkets,
        TimeProvider? time = null,
        ILogger? logger = null,
        int batchSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(listOpenMarkets);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _store = store;
        _ids = ids;
        _listOpenMarkets = listOpenMarkets;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _batchSize = batchSize;
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass found.</returns>
    public async Task<DiscoveryResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow().UtcDateTime;
        var passStart = new DateTime(now.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc); // the column holds milliseconds
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

        var result = new DiscoveryResult(seen, skipped, matched, added, removed, Stopwatch.GetElapsedTime(started));
        LogPass(_logger, result.Seen, result.Added.Count, result.Removed.Count, result.SkippedCombos, result.Duration.TotalSeconds);
        return result;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Market discovery: {Seen} open markets, {Added} new, {Removed} unlisted, {Skipped} combos skipped, {Seconds:F1} s")]
    private static partial void LogPass(ILogger logger, int seen, int added, int removed, int skipped, double seconds);
}
