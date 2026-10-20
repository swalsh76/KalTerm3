using System.Net;
using System.Text.Json;
using KalTerm.Contracts;

namespace KalTerm.Client.Tests;

public sealed class MarketStreamClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5280");

    private static string Ready(string resume = "2026-10-18T22:59:58.000Z") =>
        $"id: {resume}\nevent: ready\ndata: {ApiJson.Serialize(new StreamReadyDto("abc", resume, ["A-1"], ["A-1"], ["Z-9"], [], 10))}\n\n";

    private static string Tick(string id, string price = "0.560000") =>
        $"id: {id}\nevent: tick\ndata: {ApiJson.Serialize(new MarketTickDto("A-1", new TickDto("2026-10-18T22:59:59.000000+00:00", price, "0.550000", "0.570000", null, null, null, null, null)))}\n\n";

    private static string Book() =>
        $"event: book\ndata: {ApiJson.Serialize(new OrderBookDto("A-1", "2026-10-18T22:59:50.000000+00:00", "2026-10-18T22:59:58.000000+00:00", 2, false, false, [new BookLevelDto("0.410000", "1.50")], [new BookLevelDto("0.540000", "7.00")]))}\n\n";

    private static async Task<List<MarketStreamEvent>> ReadAllAsync(KalTermApiClient client, MarketStreamRequest request)
    {
        var items = new List<MarketStreamEvent>();
        await foreach (var item in client.StreamMarketsAsync(request, TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task TheStreamIsOneGetNamingTheMarketsAndYieldsEveryKindOfEvent()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Ready() + Book() + Tick("2026-10-18T22:59:59.000Z") + ": keep-alive\n\n" + "event: reset\ndata: {\"reason\":\"slow_consumer\"}\n\n");
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = await ReadAllAsync(client, new MarketStreamRequest(["A-1", "B-2"], ["A-1"], 5, "2026-10-18T22:50:00.000Z"));

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:5280/v1/stream?ticks=A-1%2CB-2&books=A-1&depth=5&after=2026-10-18T22%3A50%3A00.000Z", request.Uri.AbsoluteUri);
        Assert.Contains("text/event-stream", request.Headers["Accept"], StringComparison.Ordinal);
        Assert.Equal(["ReadyEvent", "BookEvent", "TickEvent", "ResetEvent"], items.Select(i => i.GetType().Name));
        Assert.Equal(("abc", "2026-10-18T22:59:58.000Z", 1), (((ReadyEvent)items[0]).Ready.StreamId, ((ReadyEvent)items[0]).Ready.ResumeFrom, ((ReadyEvent)items[0]).Ready.Unknown.Count));
        Assert.Equal("A-1", ((BookEvent)items[1]).Book.Ticker);
        var tick = (TickEvent)items[2];
        Assert.Equal(("2026-10-18T22:59:59.000Z", "A-1", "0.560000"), (tick.Id, tick.Tick.Ticker, tick.Tick.Tick.Price));
        Assert.Equal("slow_consumer", ((ResetEvent)items[3]).Reason);
    }

    [Fact]
    public async Task OnlyWhatWasAskedForIsPutInTheAddress()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Ready());
        using var client = KalTermApiClient.Create(Base, stub, null);

        await ReadAllAsync(client, new MarketStreamRequest([], ["A-1"]));

        Assert.Equal("http://127.0.0.1:5280/v1/stream?books=A-1", stub.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task EventsItDoesNotKnowOrCannotPlaceAreSkipped()
    {
        var body = "event: mystery\nid: x\ndata: {}\n\n"                       // an event type from the future
            + "event: tick\ndata: {\"ticker\":\"A-1\"}\n\n"                    // a tick without an id
            + "event: ready\ndata: null\n\n"                                    // nothing in it
            + Tick("2026-10-18T22:59:59.000Z");
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = await ReadAllAsync(client, new MarketStreamRequest(["A-1"], []));

        Assert.IsType<TickEvent>(Assert.Single(items));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "too_many_streams")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter")]
    public async Task ARefusedStreamIsAnApiExceptionWithTheServersCode(HttpStatusCode status, string code)
    {
        var stub = new StubHttpMessageHandler().Respond(status, ApiJson.Serialize(new ErrorDto(code)));
        using var client = KalTermApiClient.Create(Base, stub, null);

        var thrown = await Assert.ThrowsAsync<ApiException>(() => ReadAllAsync(client, new MarketStreamRequest(["A-1"], [])));

        Assert.Equal((status, code), (thrown.Status, thrown.Code));
    }

    [Fact]
    public async Task AnUnreachableServerIsAnApiUnavailableException()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("no route"));
        using var client = KalTermApiClient.Create(Base, stub, null);

        await Assert.ThrowsAsync<ApiUnavailableException>(() => ReadAllAsync(client, new MarketStreamRequest(["A-1"], [])));
    }
}

public sealed class MarketWatcherTests
{
    private static MarketTickDto Tick(string price = "0.560000", string ticker = "A-1") =>
        new(ticker, new TickDto("2026-10-18T22:59:59.000000+00:00", price, null, null, null, null, null, null, null));

    private static ReadyEvent Ready(string resume) => new(new StreamReadyDto("abc", resume, ["A-1"], [], [], [], 10));

    private static TickEvent At(string id, string price = "0.560000") => new(Tick(price), id);

    private sealed class Script(params object[] steps)
    {
        private readonly Queue<object> _steps = new(steps);

        public List<MarketStreamRequest> Opens { get; } = [];

        public CancellationTokenSource Stop { get; } = new();

        public async IAsyncEnumerable<MarketStreamEvent> OpenAsync(MarketStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Opens.Add(request);
            if (!_steps.TryDequeue(out var step))
            {
                await Stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            if (step is Exception failure)
            {
                throw failure;
            }

            foreach (var item in (IEnumerable<MarketStreamEvent>)step)
            {
                yield return item;
            }
        }
    }

    private static async Task<(List<MarketStreamEvent> Events, List<string> Notices)> RunAsync(Script script, AutoAdvanceTimeProvider time, int? depth = null)
    {
        var events = new List<MarketStreamEvent>();
        var notices = new List<string>();
        var watcher = new MarketWatcher(script.OpenAsync, time);
        await watcher.RunAsync(["A-1"], ["A-1"], depth, e => { events.Add(e); return Task.CompletedTask; }, notices.Add, script.Stop.Token);
        return (events, notices);
    }

    [Fact]
    public async Task EveryTickIsHandedOverOnceInOrderAndAReopenedStreamResumesFromTheLastTick()
    {
        var script = new Script(new MarketStreamEvent[] { Ready("t0"), At("t1"), At("t2") }, new ApiUnavailableException("down"), new MarketStreamEvent[] { Ready("t3"), At("t2"), At("t3") });

        var (events, notices) = await RunAsync(script, new AutoAdvanceTimeProvider(), 7);

        Assert.Equal(["t1", "t2", "t3"], events.OfType<TickEvent>().Select(t => t.Id));   // the repeat of t2 after the reconnect was dropped
        Assert.Equal([null, "t2", "t2", "t3"], script.Opens.Select(o => o.ResumeFrom));
        Assert.All(script.Opens, open =>
        {
            Assert.Equal(["A-1"], open.Ticks);
            Assert.Equal(["A-1"], open.Books);
            Assert.Equal(7, open.Depth);
        });
        Assert.Equal(["the server closed the stream; reconnecting", "down", "the server closed the stream; reconnecting"], notices);
    }

    [Fact]
    public async Task TheFirstConnectionResumesFromWhereLiveDataBeganIfNoTickCameYet()
    {
        var script = new Script((object)new MarketStreamEvent[] { Ready("t0") });

        await RunAsync(script, new AutoAdvanceTimeProvider());

        Assert.Equal([null, "t0"], script.Opens.Select(o => o.ResumeFrom));
    }

    [Fact]
    public async Task TwoDifferentTicksAtTheSameMomentAreBothDeliveredButNotRepeated()
    {
        var script = new Script(new MarketStreamEvent[] { Ready("t0"), At("t1", "0.10"), At("t1", "0.20") }, new MarketStreamEvent[] { Ready("t1"), At("t1", "0.10"), At("t1", "0.20"), At("t1", "0.30") });

        var (events, _) = await RunAsync(script, new AutoAdvanceTimeProvider());

        Assert.Equal(["0.10", "0.20", "0.30"], events.OfType<TickEvent>().Select(t => t.Tick.Tick.Price));
    }

    [Fact]
    public async Task AResetIsPassedOnAndTheStreamIsOpenedAfreshAtOnce()
    {
        var script = new Script(new MarketStreamEvent[] { Ready("t0"), At("t1"), new ResetEvent("too_far_behind") }, new MarketStreamEvent[] { Ready("t9"), At("t9") });
        var time = new AutoAdvanceTimeProvider();

        var (events, notices) = await RunAsync(script, time);

        Assert.Contains(events, e => e is ResetEvent { Reason: "too_far_behind" });
        Assert.Equal([null, null, "t9"], script.Opens.Take(3).Select(o => o.ResumeFrom));   // no resume point after a reset
        Assert.Equal("the server asked for a reload; reconnecting", notices[0]);
    }

    [Fact]
    public async Task TheWaitBetweenAttemptsDoublesUpToHalfAMinuteAndStartsOverAfterSomethingArrives()
    {
        var down = () => new ApiUnavailableException("down");
        var script = new Script(down(), down(), down(), down(), down(), down(), down(), new MarketStreamEvent[] { Ready("t0"), At("t1") }, down());
        var time = new AutoAdvanceTimeProvider();

        await RunAsync(script, time);

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 1, 2], time.Delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task ADroppedConnectionIsReopenedAndTooManyStreamsWaitsHalfAMinute()
    {
        var script = new Script(new IOException("reset"), new ApiException(HttpStatusCode.TooManyRequests, "too_many_streams"), new ApiException(HttpStatusCode.BadGateway, "http_error"), new JsonException("garbage"));

        var (_, notices) = await RunAsync(script, new AutoAdvanceTimeProvider());

        Assert.Equal(["the connection dropped: reset", "the server has too many streams open for you; waiting", "the server answered 502; waiting", "the server sent something unreadable; reconnecting"], notices);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter")]
    public async Task ARefusalThatWaitingWillNotCureEndsTheWatch(HttpStatusCode status, string code)
    {
        var script = new Script(new ApiException(status, code));

        var thrown = await Assert.ThrowsAsync<ApiException>(() => RunAsync(script, new AutoAdvanceTimeProvider()));

        Assert.Equal(code, thrown.Code);
        Assert.Single(script.Opens);
    }

    [Fact]
    public async Task CancellingTheWatchEndsItQuietlyEvenInTheMiddleOfAWait()
    {
        var script = new Script(new ApiUnavailableException("down"));
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        using var stop = new CancellationTokenSource();
        var watcher = new MarketWatcher(script.OpenAsync, time);

        var run = watcher.RunAsync(["A-1"], [], null, _ => Task.CompletedTask, _ => { }, stop.Token);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (time.PendingTimers != 1)
        {
            await Task.Delay(5, guard.Token);
        }

        await stop.CancelAsync();

        await run;
        Assert.Single(script.Opens);
    }
}
