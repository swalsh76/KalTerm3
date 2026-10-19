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

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static MarketDiscovery WithIncremental(FakeMarketStore store, MarketIdMap ids, AutoAdvanceTimeProvider clock, IEnumerable<KalTerm.Kalshi.Models.Market> full, UpdatedListing updated, TimeSpan? overlap = null) =>
        new(store, ids, Listing(full), clock, NullLogger.Instance, 1000, updated.Call, overlap);

    [Fact]
    public async Task AnIncrementalPassBeforeAnyPassRunsAsFull()
    {
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing();
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), clock, [Open("A")], updated);

        var result = await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(PassKind.Full, result.Kind);
        Assert.Empty(updated.Since);
        Assert.Equal(Start.UtcDateTime, discovery.Bookmark);
    }

    [Fact]
    public async Task NoPassHasABookmarkYet()
    {
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), new AutoAdvanceTimeProvider(), [], new UpdatedListing());

        Assert.Null(discovery.Bookmark);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EachIncrementalPassLooksBackFromTheLastPassByTheOverlap()
    {
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing();
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), clock, [Open("A")], updated);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));

        await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal([Start.AddMinutes(-2), Start.AddSeconds(30).AddMinutes(-2)], updated.Since);
    }

    [Fact]
    public async Task AnIncrementalPassStoresKnownMarketsAndNewActiveOnesAndIgnoresTheRest()
    {
        var store = new FakeMarketStore();
        var ids = new MarketIdMap();
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing([
            Open("A").With("finalized"),        // known, settled: stored with its true status, reported as no longer tradable
            Open("B"),                          // known, still active: refreshed
            Open("C"),                          // unknown, active: a new market
            Open("D").With("finalized"),        // unknown, settled history: ignored
            Open("E").With("initialized"),      // unknown, not open yet: ignored
            Combo("KXMVE-1"),                   // a combo that slipped through: skipped
        ]);
        var discovery = WithIncremental(store, ids, clock, [Open("A"), Open("B")], updated);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        var unlistCalls = store.UnlistCalls;
        clock.Advance(TimeSpan.FromSeconds(30));

        var result = await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(PassKind.Incremental, result.Kind);
        Assert.Equal(3, result.Seen);
        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Ignored);
        Assert.Equal(1, result.SkippedCombos);
        Assert.Equal(["C"], result.Added);
        Assert.Equal(["A"], result.Removed);
        Assert.Equal("finalized", store.Statuses["A"]);
        Assert.Equal("active", store.Statuses["C"]);
        Assert.DoesNotContain("D", store.Statuses.Keys);
        Assert.DoesNotContain("E", store.Statuses.Keys);
        Assert.Equal(unlistCalls, store.UnlistCalls);   // an incremental pass never unlists
        Assert.True(ids.TryGetId("C", out _));
    }

    [Fact]
    public async Task AnIncrementalPassThatFindsNothingChangedIsNormalAndAdvancesTheBookmark()
    {
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing();
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), clock, [Open("A")], updated);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));

        var result = await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Seen);
        Assert.Empty(result.Removed);
        Assert.Equal(Start.UtcDateTime.AddSeconds(30), discovery.Bookmark);
    }

    [Fact]
    public async Task AFailedIncrementalPassKeepsTheBookmarkSoTheNextOneCoversTheSameGround()
    {
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing([Open("A")]) { FailWith = new HttpRequestException("down") };
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), clock, [Open("A")], updated);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<HttpRequestException>(() => discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken));
        updated.FailWith = null;
        clock.Advance(TimeSpan.FromSeconds(30));
        await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(Start.UtcDateTime.AddSeconds(60), discovery.Bookmark);
        Assert.Equal(updated.Since[0], updated.Since[1]);   // both look back from the full pass
    }

    [Fact]
    public async Task AnIncrementalPassWithoutAListingIsRefused()
    {
        var discovery = new MarketDiscovery(new FakeMarketStore(), new MarketIdMap(), Listing([Open("A")]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnIncrementalPassCanBeCancelled()
    {
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var updated = new UpdatedListing([Open("A")]);
        var discovery = WithIncremental(new FakeMarketStore(), new MarketIdMap(), clock, [Open("A")], updated);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.RunAsync(PassKind.Incremental, cts.Token));
    }

    [Fact]
    public async Task IncrementalPassesWriteInBatches()
    {
        var store = new FakeMarketStore();
        var clock = new AutoAdvanceTimeProvider(utcStart: Start);
        var changed = Enumerable.Range(0, 5).Select(i => Open("N" + i)).ToList();
        var updated = new UpdatedListing(changed);
        var discovery = new MarketDiscovery(store, new MarketIdMap(), Listing([Open("A")]), clock, NullLogger.Instance, 2, updated.Call);
        await discovery.RunAsync(TestContext.Current.CancellationToken);
        store.BatchSizes.Clear();
        clock.Advance(TimeSpan.FromSeconds(30));

        await discovery.RunAsync(PassKind.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal([2, 2, 1], store.BatchSizes);
    }

    [Fact]
    public void ANegativeOverlapIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketDiscovery(new FakeMarketStore(), new MarketIdMap(), Listing([]), overlap: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void TheDefaultOverlapIsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), MarketDiscovery.DefaultOverlap);
    }
}
