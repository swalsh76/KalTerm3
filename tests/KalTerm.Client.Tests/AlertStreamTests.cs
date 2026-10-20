using System.Net;
using System.Text.Json;
using KalTerm.Contracts;

namespace KalTerm.Client.Tests;

public sealed class AlertStreamClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5280");

    private static string Fire(long id, string value = "0.610000") =>
        $"id: {id}\nevent: fire\ndata: {ApiJson.Serialize(new AlertFireDto(id, 7, "A-1", "price_above", "0.600000", value, "2026-10-18T22:59:00.250000+00:00"))}\n\n";

    private static async Task<List<AlertStreamEvent>> ReadAllAsync(KalTermApiClient client, long? after = null)
    {
        var items = new List<AlertStreamEvent>();
        await foreach (var item in client.StreamAlertFiresAsync(after, TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task TheStreamIsOneGetThatAsksForEventsAndYieldsTheReadyEventAndEachFire()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "id: 40\nevent: ready\ndata: {}\n\n" + Fire(41) + ": keep-alive\n\n" + Fire(42, "0.620000"));
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = await ReadAllAsync(client, 39);

        var request = Assert.Single(stub.Requests);
        Assert.Equal((HttpMethod.Get, "http://127.0.0.1:5280/v1/alerts/stream?after=39"), (request.Method, request.Uri.AbsoluteUri));
        Assert.Contains("text/event-stream", request.Headers["Accept"], StringComparison.Ordinal);
        Assert.Equal([40L, 41L, 42L], items.Select(i => i.Id));
        Assert.Equal([null, "0.610000", "0.620000"], items.Select(i => i.Fire?.Value));
    }

    [Fact]
    public async Task WithoutAnAfterNoParameterIsSentAndCarriageReturnsAreFine()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "id: 0\r\nevent: ready\r\ndata: {}\r\n\r\n");
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = await ReadAllAsync(client);

        Assert.Equal("http://127.0.0.1:5280/v1/alerts/stream", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(new AlertStreamEvent(0, null), Assert.Single(items));
    }

    [Fact]
    public async Task EventsItDoesNotKnowOrCannotPlaceAreSkipped()
    {
        var body = "event: mystery\nid: 5\ndata: {}\n\n"   // an event type from the future
            + "event: fire\ndata: {\"id\":9}\n\n"          // no id line
            + "id: x\nevent: fire\ndata: {}\n\n"           // an id that is not a number
            + "id: 6\nevent: ready\ndata: {}\n\n";
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        using var client = KalTermApiClient.Create(Base, stub, null);

        var items = await ReadAllAsync(client);

        Assert.Equal(new AlertStreamEvent(6, null), Assert.Single(items));
    }

    [Fact]
    public async Task ADataFieldSplitOverLinesIsJoinedWithNewlines()
    {
        var json = ApiJson.Serialize(new AlertFireDto(3, 7, "A-1", "price_above", "0.600000", "0.610000", "2026-10-18T22:59:00.250000+00:00"));
        var comma = json.IndexOf(',', StringComparison.Ordinal) + 1;
        var body = $"id: 3\nevent: fire\ndata: {json[..comma]}\ndata: {json[comma..]}\n\n";
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        using var client = KalTermApiClient.Create(Base, stub, null);

        var item = Assert.Single(await ReadAllAsync(client));

        Assert.Equal(("A-1", "0.610000"), (item.Fire!.Ticker, item.Fire.Value));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "too_many_streams")]
    public async Task ARefusedStreamIsAnApiExceptionWithTheServersCode(HttpStatusCode status, string code)
    {
        var stub = new StubHttpMessageHandler().Respond(status, ApiJson.Serialize(new ErrorDto(code)));
        using var client = KalTermApiClient.Create(Base, stub, null);

        var thrown = await Assert.ThrowsAsync<ApiException>(() => ReadAllAsync(client));

        Assert.Equal((status, code), (thrown.Status, thrown.Code));
    }

    [Fact]
    public async Task AnUnreachableServerIsAnApiUnavailableException()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("no route"));
        using var client = KalTermApiClient.Create(Base, stub, null);

        await Assert.ThrowsAsync<ApiUnavailableException>(() => ReadAllAsync(client));
    }
}

public sealed class AlertWatcherTests
{
    private static AlertFireDto Fire(long id) => new(id, 7, "A-1", "price_above", "0.600000", "0.610000", "2026-10-18T22:59:00.250000+00:00");

    private sealed class Script(params object[] steps)
    {
        private readonly Queue<object> _steps = new(steps);

        public List<long?> Opens { get; } = [];

        public CancellationTokenSource Stop { get; } = new();

        public async IAsyncEnumerable<AlertStreamEvent> OpenAsync(long? after, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Opens.Add(after);
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

            foreach (var item in (IEnumerable<AlertStreamEvent>)step)
            {
                yield return item;
            }
        }
    }

    private static AlertStreamEvent[] Events(long ready, params long[] fires) => [new AlertStreamEvent(ready, null), .. fires.Select(id => new AlertStreamEvent(id, Fire(id)))];

    private static async Task<(List<long> Fires, List<string> Notices)> RunAsync(Script script, AutoAdvanceTimeProvider time, long? after = null)
    {
        var fires = new List<long>();
        var notices = new List<string>();
        var watcher = new AlertWatcher(script.OpenAsync, time);
        await watcher.RunAsync(after, f => { fires.Add(f.Id); return Task.CompletedTask; }, notices.Add, script.Stop.Token);
        return (fires, notices);
    }

    [Fact]
    public async Task EveryFireIsHandedOverOnceInOrderAndAReopenedStreamResumesFromTheLastId()
    {
        var script = new Script(Events(10, 11, 12), new ApiUnavailableException("down"), Events(12, 13));
        var time = new AutoAdvanceTimeProvider();

        var (fires, notices) = await RunAsync(script, time);

        Assert.Equal([11L, 12L, 13L], fires);
        Assert.Equal<long?>([null, 12, 12, 13], script.Opens);
        Assert.Equal(["the server closed the stream; reconnecting", "down", "the server closed the stream; reconnecting"], notices);
    }

    [Fact]
    public async Task TheWaitBetweenAttemptsDoublesUpToHalfAMinuteAndStartsOverAfterSomethingArrives()
    {
        var down = () => new ApiUnavailableException("down");
        var script = new Script(down(), down(), down(), down(), down(), down(), down(), Events(5, 6), down());
        var time = new AutoAdvanceTimeProvider();

        await RunAsync(script, time);

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 1, 2], time.Delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task ADroppedConnectionIsReopenedAndTooManyStreamsWaitsHalfAMinute()
    {
        var script = new Script(new IOException("reset"), new ApiException(HttpStatusCode.TooManyRequests, "too_many_streams"), new ApiException(HttpStatusCode.BadGateway, "http_error"), new JsonException("garbage"));
        var time = new AutoAdvanceTimeProvider();

        var (_, notices) = await RunAsync(script, time);

        Assert.Equal(["the connection dropped: reset", "the server has too many streams open for you; waiting", "the server answered 502; waiting", "the server sent something unreadable; reconnecting"], notices);
        Assert.Equal([1, 30, 30, 30], time.Delays.Take(4).Select(d => (int)d.TotalSeconds));   // the long wait stays long until something arrives
    }

    [Fact]
    public async Task AReplayStartsFromTheIdGiven()
    {
        var script = new Script((object)Events(9, 10));
        var time = new AutoAdvanceTimeProvider();

        await RunAsync(script, time, after: 8);

        Assert.Equal<long?>(8, script.Opens[0]);
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
        var watcher = new AlertWatcher(script.OpenAsync, time);

        var run = watcher.RunAsync(null, _ => Task.CompletedTask, _ => { }, stop.Token);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (time.PendingTimers != 1)
        {
            await Task.Delay(5, guard.Token);   // the watcher has failed once and is waiting to reconnect
        }

        await stop.CancelAsync();

        await run;
        Assert.Single(script.Opens);
    }
}
