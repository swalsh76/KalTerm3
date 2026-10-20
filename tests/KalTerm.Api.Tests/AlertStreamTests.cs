using System.Net;
using KalTerm.Contracts;
using KalTerm.Data.Alerts;
using KalTerm.Data.Users;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

public class AlertStreamHubTests
{
    private static AlertStreamHub Hub(FakeVersion version, int perUser = 2, int total = 3) => new(version, new AlertStreamOptions { MaxPerUser = perUser, MaxTotal = total });

    [Fact]
    public void AUserAndTheServerEachHaveALimitAndAFreedPlaceCanBeTakenAgain()
    {
        var hub = Hub(new FakeVersion());

        var a1 = hub.TryAcquire(1);
        var a2 = hub.TryAcquire(1);
        var a3 = hub.TryAcquire(1);        // the user's third
        var b1 = hub.TryAcquire(2);        // the server's third
        var c1 = hub.TryAcquire(3);        // the server's fourth

        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.Null(a3);
        Assert.NotNull(b1);
        Assert.Null(c1);
        Assert.Equal(3, hub.Open);
        a1.Dispose();
        a1.Dispose();   // twice is the same as once
        Assert.Equal(2, hub.Open);
        Assert.NotNull(hub.TryAcquire(3));
    }

    [Fact]
    public void TheDefaultsAreTenPerUserAndTwoHundredFiftyInAll()
    {
        var options = new AlertStreamOptions();

        Assert.Equal((10, 250, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)), (options.MaxPerUser, options.MaxTotal, options.KeepAlive, options.TokenCheck));
    }

    [Fact]
    public async Task AWaitingStreamIsWokenWhenTheCounterMovesAndNotBefore()
    {
        var version = new FakeVersion();
        var hub = Hub(version);
        await hub.PollAsync(TestContext.Current.CancellationToken);
        var seen = hub.Version;

        var waiting = hub.WaitForChangeAsync(seen, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await hub.PollAsync(TestContext.Current.CancellationToken);   // nothing moved
        Assert.False(waiting.IsCompleted);
        version.Value = 5;
        await hub.PollAsync(TestContext.Current.CancellationToken);

        Assert.True(await waiting);
        Assert.Equal(5, hub.Version);
    }

    [Fact]
    public async Task ACounterThatMovedBeforeTheWaitBeganEndsTheWaitAtOnce()
    {
        var version = new FakeVersion { Value = 3 };
        var hub = Hub(version);
        await hub.PollAsync(TestContext.Current.CancellationToken);

        Assert.True(await hub.WaitForChangeAsync(0, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANoWakeUpIsATimeout()
    {
        var hub = Hub(new FakeVersion());
        await hub.PollAsync(TestContext.Current.CancellationToken);

        Assert.False(await hub.WaitForChangeAsync(hub.Version, TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedReadKeepsTheCounterAndCarriesOn()
    {
        var version = new FakeVersion { Value = 7 };
        var hub = Hub(version);
        await hub.PollAsync(TestContext.Current.CancellationToken);

        version.Fails = new InvalidOperationException("database down");
        await hub.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, hub.Version);
        version.Fails = null;
        version.Value = 8;
        await hub.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, hub.Version);
    }

    [Fact]
    public async Task TheWaitEndsWhenItIsCancelled()
    {
        var hub = Hub(new FakeVersion());
        using var cancel = new CancellationTokenSource();
        var waiting = hub.WaitForChangeAsync(hub.Version, TimeSpan.FromSeconds(10), cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

/// <summary>The alert stream over a test server: what a client reads, in what order, and when it is turned away or cut off.</summary>
public sealed class AlertStreamEndpointTests : IAsyncLifetime
{
    private ApiTestHost _api = null!;

    private FakeReaders Fakes => _api.Fakes;

    public async ValueTask InitializeAsync() => _api = await ApiTestHost.StartAsUserAsync(42);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static UserFire Fire(long id, long value = 610_000) => new(id, 7, "KX-1", AlertKind.PriceAbove, 600_000, value, NowUtc.AddSeconds(id));

    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(ApiTestHost host, string url = "/v1/alerts/stream", string? lastEventId = null, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return (response, new StreamReader(stream));
    }

    /// <summary>The next event as its lines (without the blank line that ends it); comments count as events here.</summary>
    private static async Task<List<string>> NextAsync(StreamReader reader)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            if (line.Length == 0)
            {
                return lines;
            }

            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task TheStreamStartsWithAReadyEventNamingTheNewestFireAndSaysItIsAnEventStream()
    {
        Fakes.UserData.Fires = [Fire(40), Fire(41)];

        var (response, reader) = await OpenAsync(_api);

        Assert.Equal(("text/event-stream", "no-cache"), (response.Content.Headers.ContentType!.MediaType, response.Headers.CacheControl!.ToString()));
        Assert.Equal(["id: 41", "event: ready", "data: {}"], await NextAsync(reader));
        response.Dispose();
    }

    [Fact]
    public async Task AFireThatHappensAfterTheStreamOpenedArrivesOnceWhenTheCounterMoves()
    {
        Fakes.UserData.Fires = [Fire(41)];
        var (response, reader) = await OpenAsync(_api);
        await NextAsync(reader);

        Fakes.UserData.Fires = [Fire(41), Fire(42, 620_000)];
        Fakes.Version.Value = 1;
        await Fakes.Hub.PollAsync(TestContext.Current.CancellationToken);
        var fire = await NextAsync(reader);

        Assert.Equal(["id: 42", "event: fire"], fire.Take(2));
        var dto = ApiJson.Deserialize<AlertFireDto>(fire[2]["data: ".Length..])!;
        Assert.Equal((42L, 7, "KX-1", "price_above", "0.600000", "0.620000"), (dto.Id, dto.AlertId, dto.Ticker, dto.Kind, dto.Threshold, dto.Value));
        Assert.Equal([": keep-alive"], await NextAsync(reader));   // the fire was sent once, not again
        response.Dispose();
    }

    [Fact]
    public async Task AfterReplaysWhatCameAfterThatIdBeforeAnythingNew()
    {
        Fakes.UserData.Fires = [Fire(40), Fire(41), Fire(42)];

        var (response, reader) = await OpenAsync(_api, "/v1/alerts/stream?after=40");

        Assert.Equal(["id: 40", "event: ready", "data: {}"], await NextAsync(reader));
        Assert.Equal("id: 41", (await NextAsync(reader))[0]);
        Assert.Equal("id: 42", (await NextAsync(reader))[0]);
        response.Dispose();
    }

    [Fact]
    public async Task ALastEventIdHeaderResumesTheSameWay()
    {
        Fakes.UserData.Fires = [Fire(40), Fire(41)];

        var (response, reader) = await OpenAsync(_api, lastEventId: "40");

        Assert.Equal("id: 40", (await NextAsync(reader))[0]);
        Assert.Equal("id: 41", (await NextAsync(reader))[0]);
        response.Dispose();
    }

    [Fact]
    public async Task AQuietStreamSendsAKeepAliveComment()
    {
        var (response, reader) = await OpenAsync(_api);
        await NextAsync(reader);

        Assert.Equal([": keep-alive"], await NextAsync(reader));
        response.Dispose();
    }

    [Fact]
    public async Task OnlyTheCallersOwnFiresAreEverRead()
    {
        var (response, reader) = await OpenAsync(_api);
        await NextAsync(reader);

        Assert.All(Fakes.UserData.Calls, call => Assert.Contains(" 42 ", call + " ", StringComparison.Ordinal));
        response.Dispose();
    }

    [Fact]
    public async Task AUserOverTheLimitIsTurnedAwayWithATryAgainLaterAndGetsInOnceAStreamEnds()
    {
        var (first, firstReader) = await OpenAsync(_api);
        var (second, secondReader) = await OpenAsync(_api);
        await NextAsync(firstReader);
        await NextAsync(secondReader);

        var (third, _) = await OpenAsync(_api);
        var body = await third.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("30", third.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("too_many_streams", ApiJson.Deserialize<ErrorDto>(body)!.Error);
        first.Dispose();
        await Task.Delay(300, TestContext.Current.CancellationToken);   // the server notices the client went away at its next keep-alive
        var (again, againReader) = await OpenAsync(_api);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("event: ready", (await NextAsync(againReader))[1]);
        second.Dispose();
        again.Dispose();
    }

    [Fact]
    public async Task WithoutAUserThereIsNoStream()
    {
        await using var anonymous = await ApiTestHost.StartAsync();

        var (response, _) = await OpenAsync(anonymous);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, anonymous.Fakes.Hub.Open);
    }

    [Fact]
    public async Task ABadAfterIsRefusedBeforeAnyStreamOpens()
    {
        var (response, _) = await OpenAsync(_api, "/v1/alerts/stream?after=abc");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, Fakes.Hub.Open);
    }

    [Fact]
    public async Task ATokenRevokedWhileStreamingEndsTheStreamAtItsNextTokenCheck()
    {
        var users = new FakeUsers();
        var token = users.Add("ann");
        var time = new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now);
        await using var host = await ApiTestHost.StartWithAuthAsync(users, time);
        var (response, reader) = await OpenAsync(host, authorization: "Bearer " + token);
        Assert.Equal("event: ready", (await NextAsync(reader))[1]);

        users.Tokens.Remove(token);
        time.Advance(TimeSpan.FromSeconds(61));   // past the token cache and the stream's own check interval
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(guard.Token);
        }
        while (line is not null);

        Assert.Null(line);   // the server ended the stream
        response.Dispose();
    }

    [Fact]
    public async Task TheStreamIsPartOfTheAuthenticatedApiAndNeedsABearerToken()
    {
        var users = new FakeUsers();
        users.Add("ann");
        await using var host = await ApiTestHost.StartWithAuthAsync(users, new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now));

        var (response, _) = await OpenAsync(host);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
