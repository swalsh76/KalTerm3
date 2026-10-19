using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;

namespace KalTerm.Ingest.Tests;

internal static class IngestTestKit
{
    public static Market Open(string ticker) => new(ticker, "EV-" + ticker, "binary", "active", "Yes", "No");

    public static Market Combo(string ticker) => Open(ticker) with { MveCollectionTicker = "KXMVE-COLLECTION" };

    public static async IAsyncEnumerable<Market> List(IEnumerable<Market> markets, Exception? thenFail = null)
    {
        foreach (var market in markets)
        {
            await Task.Yield();
            yield return market;
        }

        if (thenFail is not null)
        {
            throw thenFail;
        }
    }

    public static Func<CancellationToken, IAsyncEnumerable<Market>> Listing(IEnumerable<Market> markets, Exception? thenFail = null) =>
        _ => List(markets, thenFail);

}

/// <summary>An in-memory <see cref="IMarketStore"/> that records how it was called.</summary>
internal sealed class FakeMarketStore : IMarketStore
{
    private readonly Dictionary<string, (int Id, string Status, DateTime LastSeen)> _rows = new(StringComparer.Ordinal);
    private int _nextId = 1;

    public List<int> BatchSizes { get; } = [];

    public int UnlistCalls { get; private set; }

    public DateTime? LastPassStart { get; private set; }

    public Exception? FailUpsertWith { get; set; }

    public IReadOnlyDictionary<string, string> Statuses => _rows.ToDictionary(r => r.Key, r => r.Value.Status, StringComparer.Ordinal);

    public Task<MarketUpsertResult> UpsertAsync(IReadOnlyList<MarketRecord> records, DateTime seenAtUtc, CancellationToken cancellationToken = default)
    {
        if (FailUpsertWith is not null)
        {
            throw FailUpsertWith;
        }

        BatchSizes.Add(records.Count);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var inserted = new List<string>();
        var matched = 0;
        foreach (var record in records)
        {
            if (_rows.TryGetValue(record.Ticker, out var existing))
            {
                matched++;
                _rows[record.Ticker] = (existing.Id, record.Status, seenAtUtc);
            }
            else
            {
                inserted.Add(record.Ticker);
                _rows[record.Ticker] = (_nextId++, record.Status, seenAtUtc);
            }

            ids[record.Ticker] = _rows[record.Ticker].Id;
        }

        return Task.FromResult(new MarketUpsertResult(ids, inserted, matched));
    }

    public Task<IReadOnlyList<string>> MarkUnlistedAsync(DateTime passStartedUtc, CancellationToken cancellationToken = default)
    {
        UnlistCalls++;
        LastPassStart = passStartedUtc;
        var gone = _rows.Where(r => MarketStore.TradableStatuses.Contains(r.Value.Status) && r.Value.LastSeen < passStartedUtc).Select(r => r.Key).Order().ToList();
        foreach (var ticker in gone)
        {
            _rows[ticker] = (_rows[ticker].Id, MarketStore.UnlistedStatus, _rows[ticker].LastSeen);
        }

        return Task.FromResult<IReadOnlyList<string>>(gone);
    }

    public Task<IReadOnlyDictionary<string, int>> LoadIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(_rows.ToDictionary(r => r.Key, r => r.Value.Id, StringComparer.Ordinal));
}
