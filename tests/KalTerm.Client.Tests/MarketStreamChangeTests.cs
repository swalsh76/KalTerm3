using System.Net;
using System.Threading.Channels;
using KalTerm.Contracts;

namespace KalTerm.Client.Tests;

/// <summary>The calls that change an open stream, and the two events that confirm them.</summary>
public sealed class MarketStreamChangeClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5280");

    [Theory]
    [InlineData(StreamKind.Ticks, "ticks")]
    [InlineData(StreamKind.Books, "books")]
    public async Task AddingAndRemovingArePutAndDeleteOnTheStreamsOwnAddress(StreamKind kind, string path)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.NoContent, string.Empty).Respond(HttpStatusCode.NoContent, string.Empty);
        using var client = KalTermApiClient.Create(Base, stub, null);

        await client.AddStreamMarketAsync("abc123", kind, "KX-1", TestContext.Current.CancellationToken);
        await client.RemoveStreamMarketAsync("abc123", kind, "KX-1", TestContext.Current.CancellationToken);

        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], stub.Requests.Select(r => r.Method));
        Assert.All(stub.Requests, r => Assert.Equal($"http://127.0.0.1:5280/v1/stream/abc123/{path}/KX-1", r.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.Conflict, "too_many_markets")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter")]
    public async Task ARefusedChangeIsAnApiExceptionWithTheServersCode(HttpStatusCode status, string code)
    {
        var stub = new StubHttpMessageHandler().Respond(status, ApiJson.Serialize(new ErrorDto(code)));
        using var client = KalTermApiClient.Create(Base, stub, null);

        var thrown = await Assert.ThrowsAsync<ApiException>(() => client.AddStreamMarketAsync("abc123", StreamKind.Ticks, "KX-1", TestContext.Current.CancellationToken));

        Assert.Equal((status, code), (thrown.Status, thrown.Code));
    }

    [Fact]
    public async Task TheConfirmationsAreYieldedAsEventsWithTheirDetails()
    {
        var added = new StreamSubscriptionDto("ticks", "KX-2", "2026-10-18T22:59:58.000Z", false, false);
        var removed = new StreamSubscriptionDto("books", "KX-3", null, false, false);
        var body = $"event: subscribed\ndata: {ApiJson.Serialize(added)}\n\nevent: unsubscribed\ndata: {ApiJson.Serialize(removed)}\n\nevent: subscribed\ndata: null\n\n";
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = new List<MarketStreamEvent>();
        await foreach (var item in client.StreamMarketsAsync(new MarketStreamRequest(["KX-1"], []), TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal([new SubscribedEvent(added), new UnsubscribedEvent(removed)], items);   // the one with nothing in it was skipped
    }
}

/// <summary>The watcher's list: what it asks the server for live, what it keeps for the next connection, and what it does when the server says no.</summary>
public sealed class MarketWatcherChangeTests
{
    /// <summary>Streams the test controls: every opening gets its own channel, which the test fills and completes.</summary>
    private sealed class Live
    {
        private readonly Lock _sync = new();

        public List<MarketStreamRequest> Opens { get; } = [];

        public List<Channel<MarketStreamEvent>> Channels { get; } = [];

        public List<string> Changes { get; } = [];

        public Exception? ChangeFailure { get; set; }

        public Func<MarketStreamRequest, Task>? OnOpen { get; set; }

        public MarketWatcher? Watcher { get; set; }

        public async IAsyncEnumerable<MarketStreamEvent> OpenAsync(MarketStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<MarketStreamEvent>();
            lock (_sync)
            {
                Opens.Add(request);
                Channels.Add(channel);
            }

            if (OnOpen is not null)
            {
                await OnOpen(request);
            }

            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }

        public Task Change(string streamId, StreamKind kind, string ticker, bool add, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Changes.Add($"{(add ? "add" : "remove")} {streamId} {kind.ToString().ToLowerInvariant()} {ticker}");
            }

            return ChangeFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public async Task WaitForOpensAsync(int count)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (_sync)
                {
                    if (Opens.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(5, guard.Token);
            }
        }
    }

    private static ReadyEvent Ready(string id = "abc") => new(new StreamReadyDto(id, "t0", ["A-1"], [], [], [], 10));

    private sealed class Run : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Run(Live live, IReadOnlyList<string> ticks, IReadOnlyList<string> books, TimeProvider? time = null, bool withChange = true)
        {
            Live = live;
            Watcher = new MarketWatcher(live.OpenAsync, time ?? new AutoAdvanceTimeProvider(), withChange ? live.Change : null);
            live.Watcher = Watcher;
            _task = Watcher.RunAsync(
                ticks,
                books,
                null,
                e =>
                {
                    Events.Add(e);
                    if (e is ReadyEvent)
                    {
                        _ready.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
                Notices.Add,
                _stop.Token);
        }

        public Live Live { get; }

        public MarketWatcher Watcher { get; }

        public List<MarketStreamEvent> Events { get; } = [];

        public List<string> Notices { get; } = [];

        public async Task ReadyAsync(string id = "abc")
        {
            await Live.WaitForOpensAsync(1);
            await Live.Channels[^1].Writer.WriteAsync(Ready(id), TestContext.Current.CancellationToken);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _task.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMarketAddedWhileConnectedIsToldToTheServerAndKeptForTheNextConnection()
    {
        var live = new Live();
        await using var run = new Run(live, ["A-1"], []);
        await run.ReadyAsync();

        await run.Watcher.AddAsync(StreamKind.Ticks, "B-2", Ct);
        await run.Watcher.AddAsync(StreamKind.Books, "B-2", Ct);
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        Assert.Equal(["add abc ticks B-2", "add abc books B-2"], live.Changes);
        Assert.Equal(["A-1", "B-2"], live.Opens[1].Ticks.Order(StringComparer.Ordinal));
        Assert.Equal(["B-2"], live.Opens[1].Books);
    }

    [Fact]
    public async Task AMarketRemovedWhileConnectedIsToldToTheServerAndLeftOutOfTheNextConnection()
    {
        var live = new Live();
        await using var run = new Run(live, ["A-1", "B-2"], ["A-1"]);
        await run.ReadyAsync();

        await run.Watcher.RemoveAsync(StreamKind.Ticks, "B-2", Ct);
        await run.Watcher.RemoveAsync(StreamKind.Books, "A-1", Ct);
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        Assert.Equal(["remove abc ticks B-2", "remove abc books A-1"], live.Changes);
        Assert.Equal(["A-1"], live.Opens[1].Ticks);
        Assert.Empty(live.Opens[1].Books);
    }

    [Fact]
    public async Task AMarketAlreadyWatchedOrNeverWatchedIsNotAskedAboutAgain()
    {
        var live = new Live();
        await using var run = new Run(live, ["A-1"], []);
        await run.ReadyAsync();

        await run.Watcher.AddAsync(StreamKind.Ticks, "A-1", Ct);
        await run.Watcher.RemoveAsync(StreamKind.Ticks, "Z-9", Ct);
        await run.Watcher.RemoveAsync(StreamKind.Books, "A-1", Ct);

        Assert.Empty(live.Changes);
    }

    [Fact]
    public async Task WithNoOpenStreamAChangeOnlyChangesTheListTheFirstConnectionAsksFor()
    {
        var live = new Live();
        var watcher = new MarketWatcher(live.OpenAsync, new AutoAdvanceTimeProvider(), live.Change);
        await watcher.AddAsync(StreamKind.Ticks, "B-2", Ct);
        using var stop = new CancellationTokenSource();
        var task = watcher.RunAsync(["A-1"], [], null, _ => Task.CompletedTask, _ => { }, stop.Token);

        await live.WaitForOpensAsync(1);
        await stop.CancelAsync();
        await task;

        Assert.Empty(live.Changes);
        Assert.Equal(["A-1", "B-2"], live.Opens[0].Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AChangeMadeAfterTheRequestWasBuiltButBeforeTheStreamWasReadyIsSentOnceItIs()
    {
        var live = new Live();
        live.OnOpen = async _ => await live.Watcher!.AddAsync(StreamKind.Ticks, "C-3", Ct);   // the stream is not open yet: only the list changes
        await using var run = new Run(live, ["A-1"], ["B-2"]);

        await run.ReadyAsync();

        Assert.Equal(["add abc ticks C-3"], live.Changes);
        Assert.Equal(["A-1"], live.Opens[0].Ticks);
    }

    [Fact]
    public async Task AMarketRemovedBeforeTheStreamWasReadyIsRemovedOnceItIs()
    {
        var live = new Live();
        live.OnOpen = async _ => await live.Watcher!.RemoveAsync(StreamKind.Books, "B-2", Ct);
        await using var run = new Run(live, ["A-1"], ["B-2"]);

        await run.ReadyAsync();

        Assert.Equal(["remove abc books B-2"], live.Changes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "too_many_markets")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter")]
    public async Task AChangeTheServerRefusesIsReportedAndLeftOffTheList(HttpStatusCode status, string code)
    {
        var live = new Live { ChangeFailure = new ApiException(status, code) };
        await using var run = new Run(live, ["A-1"], []);
        await run.ReadyAsync();

        var thrown = await Assert.ThrowsAsync<ApiException>(() => run.Watcher.AddAsync(StreamKind.Ticks, "B-2", Ct));
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        Assert.Equal(code, thrown.Code);
        Assert.Equal(["A-1"], live.Opens[1].Ticks);
    }

    [Fact]
    public async Task AStreamThatIsGoneOrAServerThatCannotBeReachedLeavesTheListAsAskedForTheNextConnectionCarriesIt()
    {
        var live = new Live { ChangeFailure = new ApiException(HttpStatusCode.NotFound, "not_found") };
        await using var run = new Run(live, ["A-1", "B-2"], []);
        await run.ReadyAsync();

        await run.Watcher.AddAsync(StreamKind.Ticks, "C-3", Ct);
        live.ChangeFailure = new ApiUnavailableException("down");
        await run.Watcher.RemoveAsync(StreamKind.Ticks, "B-2", Ct);
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        Assert.Equal(["A-1", "C-3"], live.Opens[1].Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithoutAChangeCallTheListStillChangesForTheNextConnection()
    {
        var live = new Live();
        await using var run = new Run(live, ["A-1"], [], withChange: false);
        await run.ReadyAsync();

        await run.Watcher.AddAsync(StreamKind.Books, "B-2", Ct);
        await run.Watcher.RemoveAsync(StreamKind.Ticks, "A-1", Ct);
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        Assert.Empty(live.Changes);
        Assert.Equal(["B-2"], live.Opens[1].Books);
        Assert.Empty(live.Opens[1].Ticks);
    }

    [Fact]
    public async Task ConfirmationsAndTheStreamIdAreHandedOverLikeAnyOtherEventAndTheIdIsForgottenWhenTheConnectionEnds()
    {
        var live = new Live();
        await using var run = new Run(live, ["A-1"], []);
        await run.ReadyAsync();
        var confirmation = new SubscribedEvent(new StreamSubscriptionDto("ticks", "B-2", "t1", false, false));
        await live.Channels[0].Writer.WriteAsync(confirmation, Ct);
        live.Channels[0].Writer.Complete();
        await live.WaitForOpensAsync(2);

        await run.Watcher.AddAsync(StreamKind.Ticks, "C-3", Ct);   // between connections: no call, no stream to tell

        Assert.Contains(confirmation, run.Events);
        Assert.Empty(live.Changes);
    }

    [Fact]
    public async Task WhenEveryMarketIsRemovedNothingIsOpenedUntilOneIsAdded()
    {
        var live = new Live();
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var run = new Run(live, ["A-1"], [], time);
        await run.ReadyAsync();

        await run.Watcher.RemoveAsync(StreamKind.Ticks, "A-1", Ct);
        live.Channels[0].Writer.Complete();
        await WaitUntilAsync(() => time.PendingTimers == 1);
        time.FireNext();   // the pause after a closed stream
        await WaitUntilAsync(() => time.PendingTimers == 1);
        time.FireNext();   // the pause while the list is empty
        await WaitUntilAsync(() => time.PendingTimers == 1);

        Assert.Single(live.Opens);   // an empty stream is refused by the server, so none was asked for

        await run.Watcher.AddAsync(StreamKind.Ticks, "B-2", Ct);
        await WaitUntilAsync(() =>
        {
            if (time.PendingTimers > 0)
            {
                time.FireNext();
            }

            return live.Opens.Count == 2;
        });

        Assert.Equal(["B-2"], live.Opens[1].Ticks);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, guard.Token);
        }
    }

    [Fact]
    public async Task AMissingTickerIsRefused()
    {
        var watcher = new MarketWatcher((_, _) => throw new NotSupportedException());

        await Assert.ThrowsAsync<ArgumentNullException>(() => watcher.AddAsync(StreamKind.Ticks, null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => watcher.RemoveAsync(StreamKind.Ticks, null!, Ct));
    }
}
