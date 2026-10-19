using KalTerm.Data.Markets;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.IngestTestKit;

namespace KalTerm.Ingest.Tests;

public class MarketDiscoveryTests
{
    private static MarketDiscovery Discovery(FakeMarketStore store, MarketIdMap ids, IEnumerable<KalTerm.Kalshi.Models.Market> markets, Exception? thenFail = null, int batch = 1000) =>
        new(store, ids, Listing(markets, thenFail), new AutoAdvanceTimeProvider(), NullLogger.Instance, batch);

    [Fact]
    public async Task StoresEveryOpenMarketAndFillsTheIdMap()
    {
        var store = new FakeMarketStore();
        var ids = new MarketIdMap();

        var result = await Discovery(store, ids, [Open("A"), Open("B"), Open("C")]).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Seen);
        Assert.Equal(["A", "B", "C"], result.Added);
        Assert.Empty(result.Removed);
        Assert.Equal(3, ids.Count);
        Assert.True(ids.TryGetId("B", out var id));
        Assert.Equal(2, id);
    }

    [Fact]
    public async Task SkipsAndCountsCombosThatCameBackAnyway()
    {
        var store = new FakeMarketStore();

        var result = await Discovery(store, new MarketIdMap(), [Open("A"), Combo("KXMVE-1"), Combo("KXMVE-2")]).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Seen);
        Assert.Equal(2, result.SkippedCombos);
        Assert.Equal(["A"], store.Statuses.Keys);
    }

    [Fact]
    public async Task WritesInBatches()
    {
        var store = new FakeMarketStore();
        var markets = Enumerable.Range(0, 5).Select(i => Open("M" + i)).ToList();

        await Discovery(store, new MarketIdMap(), markets, batch: 2).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal([2, 2, 1], store.BatchSizes);
    }

    [Fact]
    public async Task ASecondPassReportsWhatIsNewAndWhatWentAway()
    {
        var store = new FakeMarketStore();
        var ids = new MarketIdMap();
        var clock = new AutoAdvanceTimeProvider();
        await new MarketDiscovery(store, ids, Listing([Open("A"), Open("B"), Open("C")]), clock).RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await new MarketDiscovery(store, ids, Listing([Open("B"), Open("C"), Open("D")]), clock).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Matched);
        Assert.Equal(["D"], result.Added);
        Assert.Equal(["A"], result.Removed);
        Assert.Equal(MarketStore.UnlistedStatus, store.Statuses["A"]);
        Assert.Equal(4, ids.Count);   // an unlisted market keeps its id
    }

    [Fact]
    public async Task AListingThatFailsHalfwayUnlistsNothingAndKeepsWhatWasStored()
    {
        var store = new FakeMarketStore();
        var clock = new AutoAdvanceTimeProvider();
        await new MarketDiscovery(store, new MarketIdMap(), Listing([Open("OLD")]), clock).RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        var calls = store.UnlistCalls;

        var failing = new MarketDiscovery(store, new MarketIdMap(), Listing([Open("NEW")], thenFail: new HttpRequestException("down")), clock, batchSize: 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(calls, store.UnlistCalls);
        Assert.Equal("active", store.Statuses["OLD"]);
        Assert.Equal("active", store.Statuses["NEW"]);   // the page stored before the failure is valid and stays
    }

    [Fact]
    public async Task AFailingStoreFailsThePass()
    {
        var store = new FakeMarketStore { FailUpsertWith = new InvalidOperationException("db down") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Discovery(store, new MarketIdMap(), [Open("A")]).RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, store.UnlistCalls);
    }

    [Fact]
    public async Task AnEmptyListingUnlistsNothing()
    {
        var store = new FakeMarketStore();
        var clock = new AutoAdvanceTimeProvider();
        await new MarketDiscovery(store, new MarketIdMap(), Listing([Open("A")]), clock).RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await new MarketDiscovery(store, new MarketIdMap(), Listing([]), clock).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Seen);
        Assert.Empty(result.Removed);
        Assert.Equal("active", store.Statuses["A"]);
    }

    [Fact]
    public async Task OnlyCombosListedIsAlsoTreatedAsEmpty()
    {
        var store = new FakeMarketStore();
        var clock = new AutoAdvanceTimeProvider();
        await new MarketDiscovery(store, new MarketIdMap(), Listing([Open("A")]), clock).RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await new MarketDiscovery(store, new MarketIdMap(), Listing([Combo("KXMVE-1")]), clock).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SkippedCombos);
        Assert.Equal("active", store.Statuses["A"]);
    }

    [Fact]
    public async Task ThePassStartIsTheClockToTheMillisecond()
    {
        var store = new FakeMarketStore();
        var clock = new AutoAdvanceTimeProvider(utcStart: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234));

        await new MarketDiscovery(store, new MarketIdMap(), Listing([Open("A")]), clock).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), store.LastPassStart);
    }

    [Fact]
    public async Task CancellationStopsThePass()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery(new FakeMarketStore(), new MarketIdMap(), [Open("A")]).RunAsync(cts.Token));
    }

    [Fact]
    public void ConstructorArgumentsAreValidated()
    {
        var store = new FakeMarketStore();
        var ids = new MarketIdMap();
        var list = Listing([]);

        Assert.Throws<ArgumentNullException>(() => new MarketDiscovery(null!, ids, list));
        Assert.Throws<ArgumentNullException>(() => new MarketDiscovery(store, null!, list));
        Assert.Throws<ArgumentNullException>(() => new MarketDiscovery(store, ids, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketDiscovery(store, ids, list, batchSize: 0));
    }
}
