using System.Collections.Concurrent;

namespace KalTerm.Data.Markets;

/// <summary>
/// The in-memory ticker-to-<c>market_id</c> lookup the ingest writers use on every row. Safe for one writer (discovery) and many readers.
/// Ids never change once assigned, so entries are only ever added.
/// </summary>
public sealed class MarketIdMap
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);

    /// <summary>How many tickers are known.</summary>
    public int Count => _ids.Count;

    /// <summary>Looks up a ticker.</summary>
    /// <param name="ticker">The market ticker.</param>
    /// <param name="id">Its <c>market_id</c>.</param>
    /// <returns>Whether the ticker is known.</returns>
    public bool TryGetId(string ticker, out int id) => _ids.TryGetValue(ticker, out id);

    /// <summary>Adds or confirms entries.</summary>
    /// <param name="ids">Ticker to id.</param>
    public void AddRange(IEnumerable<KeyValuePair<string, int>> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var (ticker, id) in ids)
        {
            _ids[ticker] = id;
        }
    }

    /// <summary>Loads every known market from the store (at start-up).</summary>
    /// <param name="store">The market store.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the map is filled.</returns>
    public async Task LoadAsync(IMarketStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        AddRange(await store.LoadIdsAsync(cancellationToken).ConfigureAwait(false));
    }
}
