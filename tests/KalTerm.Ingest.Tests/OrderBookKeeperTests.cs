using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Data.Books;
using KalTerm.Data.Markets;
using KalTerm.Ingest.Books;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Ingest.Tests;

internal sealed class FakeBookStore : IBookStateStore
{
    private readonly Lock _sync = new();
    private readonly List<IReadOnlyList<BookState>> _saves = [];

    public Exception? FailWith { get; set; }

    public IReadOnlyList<IReadOnlyList<BookState>> Saves
    {
        get
        {
            lock (_sync)
            {
                return [.. _saves];
            }
        }
    }

    public IEnumerable<BookState> Rows => Saves.SelectMany(s => s);

    public Task SaveAsync(IReadOnlyList<BookState> books, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        lock (_sync)
        {
            _saves.Add(books);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeOrderBookRest : IOrderBookRest
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<string> Tickers { get; } = [];

    public Exception? FailWith { get; set; }

    /// <summary>When set, the call waits for this to complete (or for its token to be cancelled).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public OrderBook Book { get; set; } = new([new PriceLevel(Dollars.Parse("0.40"), Contracts.Parse("5"))], [new PriceLevel(Dollars.Parse("0.55"), Contracts.Parse("2"))]);

    public async Task<OrderBook> OrderBookAsync(string ticker, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        lock (Tickers)
        {
            Tickers.Add(ticker);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        return FailWith is null ? Book : throw FailWith;
    }
}

public sealed class OrderBookKeeperTests : IDisposable
{
    private readonly FakeBookStore _store = new();
    private readonly FakeOrderBookRest _rest = new();
    private readonly ScriptedSession _session = new() { OrderBookSid = 7 };
    private readonly MarketIdMap _ids = new();
    private readonly ListLogger _log = new();
    private readonly AutoAdvanceTimeProvider _clock = new(autoFire: false, utcStart: new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private OrderBookKeeper? _keeper;

    public OrderBookKeeperTests()
    {
        _ids.AddRange([KeyValuePair.Create("A", 1), KeyValuePair.Create("B", 2)]);
    }

    public void Dispose() => _keeper?.Dispose();

    private OrderBookKeeper Keeper(OrderBookKeeperOptions? options = null, TimeProvider? time = null, params string[] watch)
    {
        _keeper = new OrderBookKeeper(watch.Length == 0 ? ["A", "B"] : watch, _rest, _store, _ids, options ?? new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(200) }, time ?? _clock, _log);
        return _keeper;
    }

    private static WsMessage Msg(string type, int sid, long seq, string payload, int? id = null) =>
        new(type, Id: id, Sid: sid, Seq: seq, Msg: JsonDocument.Parse(payload).RootElement.Clone(), ReceivedAt: new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private static WsMessage Snapshot(int sid, long seq, string ticker, string yes = """[["0.4000","5.00"],["0.4100","1.50"]]""", string no = """[["0.5500","2.00"]]""", int? id = null) =>
        Msg("orderbook_snapshot", sid, seq, $$"""{"market_ticker":"{{ticker}}","yes_dollars_fp":{{yes}},"no_dollars_fp":{{no}}}""", id);

    private static WsMessage DeltaMsg(int sid, long seq, string ticker, string side, string price, string change) =>
        Msg("orderbook_delta", sid, seq, $$"""{"market_ticker":"{{ticker}}","price_dollars":"{{price}}","delta_fp":"{{change}}","side":"{{side}}"}""");

    private static WsMessage Error(int id) => new("error", Id: id, Msg: JsonDocument.Parse("""{"code":6,"msg":"nope"}""").RootElement.Clone());

    private static WsMessage Control(int sid, long seq) => new("control", Sid: sid, Seq: seq);

    private static WsMessage Reconnected(bool restored, int newSid = 9) =>
        new("reconnected", Msg: JsonDocument.Parse($$"""{"resubscribed":[{{(restored ? $$"""{"channel":"orderbook_delta","sid":{{newSid}},"old_sid":7}""" : string.Empty)}}],"failed":[],"reason":"connection_lost","dropped":0}""").RootElement.Clone());

    private OrderBookKeeper OpenWithBothBooks()
    {
        var keeper = Keeper();
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));
        keeper.Handle(Snapshot(7, 2, "B"));
        return keeper;
    }

    // ---- building and publishing ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryBookIsStaleUntilItsSnapshotAndThenItIsPublishedWithItsLevels()
    {
        var keeper = Keeper();
        keeper.SessionOpened(_session);
        Assert.Equal((2, 0, 2), (keeper.Stats.Watched, keeper.Stats.Fresh, keeper.Stats.Stale));

        keeper.Handle(Snapshot(7, 1, "A"));
        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, 1), (keeper.Stats.Fresh, keeper.Stats.Stale));
        var rows = _store.Rows.ToDictionary(r => r.MarketId);
        Assert.False(rows[1].Stale);
        Assert.Equal([new BookLevel(410_000, 150), new BookLevel(400_000, 500)], rows[1].YesLevels);
        Assert.Equal([new BookLevel(550_000, 200)], rows[1].NoLevels);
        Assert.True(rows[2].Stale);
        Assert.Empty(rows[2].YesLevels);
    }

    [Fact]
    public async Task OnlyChangedBooksAreWrittenUntilTheHeartbeatWritesThemAll()
    {
        var keeper = OpenWithBothBooks();
        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _store.Rows.Count());

        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Single(_store.Saves);   // nothing changed: no second write

        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1], _store.Saves[1].Select(r => r.MarketId));

        _clock.Advance(TimeSpan.FromSeconds(16));
        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], _store.Saves[2].Select(r => r.MarketId).Order());
        Assert.True(_store.Saves[2].All(r => r.PublishedAt > r.UpdatedAt));
    }

    [Fact]
    public async Task AWatchedMarketNotYetInTheCatalogIsSkippedAndWarnedAboutOnce()
    {
        var keeper = Keeper(watch: ["A", "NOT-DISCOVERED"]);
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(16));
        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        Assert.All(_store.Rows, r => Assert.Equal(1, r.MarketId));
        Assert.Single(_log.Lines, l => l.Contains("NOT-DISCOVERED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedWriteIsLoggedAndRetriedOnTheNextPublish()
    {
        var keeper = OpenWithBothBooks();
        _store.FailWith = new InvalidOperationException("database is down");

        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_store.Saves);
        Assert.Contains(_log.Lines, l => l.Contains("could not publish", StringComparison.Ordinal));
        Assert.Equal(0, keeper.Stats.Published);

        _store.FailWith = null;
        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _store.Rows.Count());
        Assert.Equal(2, keeper.Stats.Published);
    }

    [Fact]
    public async Task ADeltaIsAppliedAndPublishedAndTheBookIsNotApproximate()
    {
        var keeper = OpenWithBothBooks();
        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4000", "-5.00"));
        keeper.Handle(DeltaMsg(7, 4, "A", "no", "0.5600", "9.00"));

        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        var a = _store.Rows.Single(r => r.MarketId == 1);
        Assert.Equal([new BookLevel(410_000, 150)], a.YesLevels);
        Assert.Equal([new BookLevel(560_000, 900), new BookLevel(550_000, 200)], a.NoLevels);
        Assert.False(a.Approximate);
    }

    // ---- recovery -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGapAsksTheStreamForASnapshotAndTheReplyFreshensTheBookWithoutREST()
    {
        var keeper = OpenWithBothBooks();

        keeper.Handle(DeltaMsg(7, 5, "A", "yes", "0.4200", "1.00"));   // seq 3 and 4 were missed
        await Wait.UntilAsync(() => _session.SnapshotRequests.Count >= 2, "a snapshot request for each book");

        Assert.Equal(["A", "B"], _session.SnapshotRequests.Select(r => r.Ticker).Order());
        Assert.All(_session.SnapshotRequests, r => Assert.Equal(7, r.Sid));
        Assert.Equal((0, 2), (keeper.Stats.Fresh, keeper.Stats.Stale));

        keeper.Handle(Snapshot(7, 6, "A", id: _session.SnapshotRequests.Single(r => r.Ticker == "A").Id));
        keeper.Handle(Snapshot(7, 7, "B", id: _session.SnapshotRequests.Single(r => r.Ticker == "B").Id));
        await Wait.UntilAsync(() => keeper.Stats.Fresh == 2, "both books to be fresh again");

        Assert.Equal(0, _rest.Calls);
        Assert.Equal((1, 2, 0), (keeper.Stats.Gaps, (int)keeper.Stats.Resyncs, (int)keeper.Stats.RestFallbacks));
    }

    [Fact]
    public async Task ARejectedSnapshotRequestFallsBackToRESTAndTheBookIsApproximate()
    {
        var keeper = Keeper(watch: ["A"]);
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        keeper.Handle(DeltaMsg(7, 4, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => _session.SnapshotRequests.Count == 1, "the snapshot request");
        keeper.Handle(Error(_session.SnapshotRequests[0].Id));
        await Wait.UntilAsync(() => keeper.Stats.RestFallbacks == 1, "the REST fallback");
        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["A"], _rest.Tickers);
        var row = _store.Rows.Last();
        Assert.False(row.Stale);
        Assert.True(row.Approximate);
        Assert.Equal([new BookLevel(400_000, 500)], row.YesLevels);
    }

    [Fact]
    public async Task ASnapshotThatNeverArrivesFallsBackToRESTAfterTheTimeout()
    {
        var keeper = Keeper(new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(50) }, TimeProvider.System, "A");
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => keeper.Stats.RestFallbacks == 1, "the timeout and the REST fallback");

        Assert.Equal(1, _rest.Calls);
        Assert.Equal(1, keeper.Stats.Fresh);
    }

    [Fact]
    public async Task AStreamThatCannotBeAskedForASnapshotFallsBackToREST()
    {
        _session.SnapshotRequestFails = new Kalshi.WebSocket.KalshiWsException("not connected");
        var keeper = Keeper(watch: ["A"]);
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => keeper.Stats.RestFallbacks == 1, "the REST fallback");

        Assert.Contains(_log.Lines, l => l.Contains("no in-stream snapshot for A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheSessionHasNoOrderBookSubscriptionARecoveryGoesStraightToREST()
    {
        var keeper = OpenWithBothBooks();
        _session.OrderBookSid = null;   // the subscription was lost on the stream's side

        keeper.Handle(DeltaMsg(7, 5, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => _rest.Calls >= 1, "a REST call");

        Assert.Empty(_session.SnapshotRequests);
    }

    [Fact]
    public async Task ARecoveryThatFailsCompletelyLeavesTheBookStaleAndIsTriedAgainLater()
    {
        _rest.FailWith = new KalshiApiException(500, "boom");
        _session.SnapshotRequestFails = new Kalshi.WebSocket.KalshiWsException("not connected");
        var keeper = Keeper(new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(50), RetryStaleAfter = TimeSpan.FromMilliseconds(100) }, TimeProvider.System, "A");
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => keeper.Stats.ResyncFailures == 1, "the failed recovery");
        Assert.Equal(1, keeper.Stats.Stale);
        Assert.Contains(_log.Lines, l => l.Contains("could not recover", StringComparison.Ordinal));

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await keeper.PublishAsync(TestContext.Current.CancellationToken);   // a stale book with no recovery running and past the retry time gets another go
        await Wait.UntilAsync(() => _rest.Calls >= 2, "the retry");
        _rest.FailWith = null;
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => keeper.Stats.Fresh == 1, "the book to recover");
    }

    [Fact]
    public async Task ABookMessageThatCannotBeReadMarksEveryBookStaleAndStartsRecoveries()
    {
        var keeper = OpenWithBothBooks();

        keeper.Handle(Msg("orderbook_delta", 7, 3, """{"market_ticker":"A"}"""));
        await Wait.UntilAsync(() => _session.SnapshotRequests.Count >= 2, "recoveries for both books");

        Assert.Equal(2, keeper.Stats.Stale);
        Assert.Contains(_log.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    // ---- reconnects and sessions ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReconnectClearsTheBooksAndTheRestoredSubscriptionStartsAgainFromItsOwnSnapshots()
    {
        var keeper = OpenWithBothBooks();

        keeper.Handle(Reconnected(restored: true, newSid: 9));
        Assert.Equal((0, 2), (keeper.Stats.Fresh, keeper.Stats.Stale));

        keeper.Handle(Snapshot(9, 1, "A", """[["0.3000","1.00"]]""", "[]"));
        keeper.Handle(Snapshot(9, 2, "B"));
        keeper.Handle(DeltaMsg(9, 3, "A", "yes", "0.3100", "2.00"));
        await keeper.PublishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, keeper.Stats.Fresh);
        Assert.Equal([new BookLevel(310_000, 200), new BookLevel(300_000, 100)], _store.Rows.Last(r => r.MarketId == 1).YesLevels);
        Assert.Equal(0, keeper.Stats.Gaps);
    }

    [Fact]
    public void AReconnectThatDoesNotRestoreTheSubscriptionIsLoggedAndTheBooksStayStale()
    {
        var keeper = OpenWithBothBooks();

        keeper.Handle(Reconnected(restored: false));

        Assert.Equal(2, keeper.Stats.Stale);
        Assert.Contains(_log.Lines, l => l.Contains("was not restored", StringComparison.Ordinal));
    }

    [Fact]
    public void AReconnectMessageNobodyCanReadIsTreatedAsNotRestored()
    {
        var keeper = OpenWithBothBooks();

        keeper.Handle(new WsMessage("reconnected", Msg: JsonDocument.Parse("""{"reason":"x"}""").RootElement.Clone()));

        Assert.Equal(2, keeper.Stats.Stale);
    }

    [Fact]
    public async Task ControlMarkersKeepTheSequenceInStepSoACommandReplyIsNotAGap()
    {
        var keeper = Keeper(watch: ["A"]);
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));

        keeper.Handle(Control(7, 2));
        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));

        Assert.Equal(0, keeper.Stats.Gaps);
        await keeper.PublishAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, _store.Rows.Last().YesLevels.Count);
    }

    [Fact]
    public void ASessionWithNoOrderBookSubscriptionBeginsNothingAndAnErrorForAnUnknownCommandIsIgnored()
    {
        var keeper = Keeper();
        keeper.SessionOpened(new ScriptedSession { OrderBookSid = null });

        keeper.Handle(Error(999));

        Assert.Equal(2, keeper.Stats.Stale);
        Assert.Equal(0, keeper.Stats.Gaps);
    }

    [Fact]
    public async Task StoppingAbandonsRecoveriesInFlight()
    {
        _rest.Gate = new TaskCompletionSource();
        var keeper = Keeper(new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(30) }, TimeProvider.System, "A");
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));
        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => _rest.Calls == 1, "the REST call to be waiting");

        await keeper.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, keeper.Stats.ResyncFailures);
        Assert.Equal(1, keeper.Stats.Stale);
    }

    [Fact]
    public async Task ASessionClosingCancelsRecoveriesAndANewSessionRecoversNormally()
    {
        _rest.Gate = new TaskCompletionSource();
        var keeper = Keeper(new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(30) }, TimeProvider.System, "A");
        keeper.SessionOpened(_session);
        keeper.Handle(Snapshot(7, 1, "A"));
        keeper.Handle(DeltaMsg(7, 3, "A", "yes", "0.4200", "1.00"));
        await Wait.UntilAsync(() => _rest.Calls == 1, "the REST call to be waiting");

        keeper.SessionClosed();
        keeper.SessionOpened(new ScriptedSession { OrderBookSid = 11 });
        keeper.Handle(Snapshot(11, 1, "A"));

        await Wait.UntilAsync(() => keeper.Stats.Fresh == 1, "the new session's snapshot");
        Assert.Equal(0, keeper.Stats.ResyncFailures);
    }

    // ---- construction ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWatchListIsDeduplicatedAndMustNotBeEmpty()
    {
        var keeper = Keeper(watch: ["A", "A", "B"]);

        Assert.Equal(["A", "B"], keeper.WatchList);
        Assert.Throws<ArgumentException>(() => new OrderBookKeeper([], _rest, _store, _ids));
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new OrderBookKeeper(null!, _rest, _store, _ids));
        Assert.Throws<ArgumentNullException>(() => new OrderBookKeeper(["A"], null!, _store, _ids));
        Assert.Throws<ArgumentNullException>(() => new OrderBookKeeper(["A"], _rest, null!, _ids));
        Assert.Throws<ArgumentNullException>(() => new OrderBookKeeper(["A"], _rest, _store, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderBookKeeper(["A"], _rest, _store, _ids, new OrderBookKeeperOptions { SnapshotTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderBookKeeper(["A"], _rest, _store, _ids, new OrderBookKeeperOptions { RetryStaleAfter = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderBookKeeper(["A"], _rest, _store, _ids, new OrderBookKeeperOptions { HeartbeatInterval = TimeSpan.Zero }));
        var keeper = Keeper();
        Assert.Throws<ArgumentNullException>(() => keeper.SessionOpened(null!));
        Assert.Throws<ArgumentNullException>(() => keeper.Handle(null!));
    }
}
