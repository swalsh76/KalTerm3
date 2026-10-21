using System.Diagnostics;
using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest;

/// <summary>What one pass of the event titles did.</summary>
/// <param name="Refreshed">Open events read and stored (0 when this pass did not refresh them).</param>
/// <param name="LookedUp">Events asked for one by one because a market mentions them and none was stored.</param>
/// <param name="Unknown">Of those, the ones Kalshi has no event for (remembered, and asked again only after a week).</param>
/// <param name="Duration">How long the pass took.</param>
/// <param name="MarketTitles">Markets whose title changed because an event that was looked up listed them (a finished market is not in the open listing, so this is how it gets its title).</param>
public sealed record EventPassResult(int Refreshed, int LookedUp, int Unknown, TimeSpan Duration, int MarketTitles = 0);

/// <summary>Keeps the event titles current.</summary>
public interface IEventCatalog
{
    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What it did.</returns>
    Task<EventPassResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps <c>dbo.market_event</c> in step with Kalshi. Every <see cref="RefreshEvery"/> (and on the first pass) it reads all open events, about 75 requests of 200; on every pass it also asks, one event at a time, for up to
/// <see cref="LookupsPerPass"/> events that stored markets mention and no event is stored for: new markets, and the history of finished markets, which fills gradually instead of in one burst. An event Kalshi has
/// no record of is remembered as unknown and asked again only after <see cref="RetryUnknownAfter"/>. A failed request stops the pass after storing what it got, and the next pass carries on.
/// </summary>
public sealed partial class EventDiscovery : IEventCatalog
{
    private readonly IEventStore _store;
    private readonly Func<CancellationToken, IAsyncEnumerable<KalshiEvent>> _listOpen;
    private readonly Func<string, CancellationToken, Task<KalshiEvent?>> _get;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _batchSize;
    private DateTimeOffset? _lastRefresh;

    /// <summary>Creates the discovery.</summary>
    /// <param name="store">The event storage.</param>
    /// <param name="listOpen">Lists the open events (normally <c>KalshiRestClient.IterateEventsAsync</c> with <c>status=open</c>).</param>
    /// <param name="get">Reads one event with its markets nested; <see langword="null"/> when Kalshi has none (normally <c>KalshiRestClient.EventWithMarketsAsync</c>, a 404 becoming <see langword="null"/>).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives one line per pass.</param>
    /// <param name="batchSize">How many events go to the database at once.</param>
    public EventDiscovery(
        IEventStore store,
        Func<CancellationToken, IAsyncEnumerable<KalshiEvent>> listOpen,
        Func<string, CancellationToken, Task<KalshiEvent?>> get,
        TimeProvider? time = null,
        ILogger? logger = null,
        int batchSize = 500)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(listOpen);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _store = store;
        _listOpen = listOpen;
        _get = get;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _batchSize = batchSize;
    }

    /// <summary>How often the open events are all read again.</summary>
    public static TimeSpan RefreshEvery { get; } = TimeSpan.FromHours(1);

    /// <summary>The most events asked for one at a time in one pass.</summary>
    public static int LookupsPerPass => 100;

    /// <summary>How long an event Kalshi did not know is left alone.</summary>
    public static TimeSpan RetryUnknownAfter { get; } = TimeSpan.FromDays(7);

    /// <inheritdoc/>
    public async Task<EventPassResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        var refreshed = 0;
        if (_lastRefresh is null || now - _lastRefresh >= RefreshEvery)
        {
            refreshed = await RefreshOpenAsync(now.UtcDateTime, cancellationToken).ConfigureAwait(false);
            _lastRefresh = now;
        }

        var (lookedUp, unknown, marketTitles) = await LookUpMissingAsync(now, cancellationToken).ConfigureAwait(false);
        var result = new EventPassResult(refreshed, lookedUp, unknown, Stopwatch.GetElapsedTime(started), marketTitles);
        LogPass(_logger, result.Refreshed, result.LookedUp, result.Unknown, result.MarketTitles, result.Duration.TotalSeconds);
        return result;
    }

    private async Task<int> RefreshOpenAsync(DateTime fetchedAt, CancellationToken cancellationToken)
    {
        var count = 0;
        var batch = new List<EventRecord>(_batchSize);
        await foreach (var item in _listOpen(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(ToRecord(item));
            if (batch.Count >= _batchSize)
            {
                await _store.UpsertAsync(batch, fetchedAt, cancellationToken).ConfigureAwait(false);
                count += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _store.UpsertAsync(batch, fetchedAt, cancellationToken).ConfigureAwait(false);
            count += batch.Count;
        }

        return count;
    }

    private async Task<(int LookedUp, int Unknown, int MarketTitles)> LookUpMissingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var missing = await _store.MissingAsync(LookupsPerPass, now.UtcDateTime - RetryUnknownAfter, cancellationToken).ConfigureAwait(false);
        var found = new List<EventRecord>(missing.Count);
        var titles = new List<(string Ticker, string Title)>();
        var unknown = 0;
        try
        {
            foreach (var ticker in missing)
            {
                var item = await _get(ticker, cancellationToken).ConfigureAwait(false);
                if (item is null)
                {
                    found.Add(new EventRecord(ticker, string.Empty, string.Empty, string.Empty, Known: false));
                    unknown++;
                }
                else
                {
                    found.Add(ToRecord(item) with { EventTicker = ticker });
                    titles.AddRange((item.Markets ?? []).Select(market => (market.Ticker, market.Title)));
                }
            }
        }
        finally
        {
            // What was read is kept even when a later request failed; the failure itself still reaches the caller.
            if (found.Count > 0)
            {
                await _store.UpsertAsync(found, now.UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            }
        }

        var changed = titles.Count > 0 ? await _store.SetMarketTitlesAsync(titles, cancellationToken).ConfigureAwait(false) : 0;
        return (found.Count, unknown, changed);
    }

    private static EventRecord ToRecord(KalshiEvent item) => new(item.EventTicker, item.SeriesTicker, item.Title, item.SubTitle);

    [LoggerMessage(Level = LogLevel.Information, Message = "event titles: {Refreshed} open events read, {LookedUp} looked up ({Unknown} unknown to Kalshi), {MarketTitles} market titles set, {Seconds:F1} s")]
    private static partial void LogPass(ILogger logger, int refreshed, int lookedUp, int unknown, int marketTitles, double seconds);
}
