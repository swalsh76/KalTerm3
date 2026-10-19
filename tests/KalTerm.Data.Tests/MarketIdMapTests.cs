using KalTerm.Data.Markets;

namespace KalTerm.Data.Tests;

public class MarketIdMapTests
{
    [Fact]
    public void LooksUpAddedTickers()
    {
        var map = new MarketIdMap();

        map.AddRange([new("A", 1), new("B", 2)]);

        Assert.True(map.TryGetId("B", out var id));
        Assert.Equal(2, id);
        Assert.False(map.TryGetId("Z", out _));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void TickersAreCaseSensitive()
    {
        var map = new MarketIdMap();
        map.AddRange([new("abc", 1)]);

        Assert.False(map.TryGetId("ABC", out _));
    }

    [Fact]
    public async Task LoadsEverythingTheStoreKnows()
    {
        var map = new MarketIdMap();

        await map.LoadAsync(new FixedStore(new Dictionary<string, int> { ["A"] = 7, ["B"] = 9 }), TestContext.Current.CancellationToken);

        Assert.Equal(2, map.Count);
        Assert.True(map.TryGetId("A", out var id));
        Assert.Equal(7, id);
    }

    [Fact]
    public async Task NullArgumentsAreRefused()
    {
        var map = new MarketIdMap();

        Assert.Throws<ArgumentNullException>(() => map.AddRange(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => map.LoadAsync(null!, TestContext.Current.CancellationToken));
    }

    private sealed class FixedStore(Dictionary<string, int> ids) : IMarketStore
    {
        public Task<MarketUpsertResult> UpsertAsync(IReadOnlyList<MarketRecord> records, DateTime seenAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> MarkUnlistedAsync(DateTime passStartedUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> LoadIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(ids);
    }
}
