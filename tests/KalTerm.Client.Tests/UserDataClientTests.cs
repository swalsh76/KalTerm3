using System.Net;
using KalTerm.Contracts;

namespace KalTerm.Client.Tests;

/// <summary>What the client sends for the caller's own watchlists, alerts and time zone, and what it does with the answers.</summary>
public sealed class UserDataClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5280");

    private static KalTermApiClient Client(StubHttpMessageHandler stub) => KalTermApiClient.Create(Base, stub, null);

    private static string Json<T>(T value) => ApiJson.Serialize(value);

    private static string Error(string code) => Json(new ErrorDto(code));

    [Fact]
    public async Task WatchlistsAreReadWithGet()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new WatchlistListDto([new WatchlistSummaryDto("favorites", 2, "2026-10-18T20:00:00.000000+00:00")])))
            .Respond(HttpStatusCode.OK, Json(new WatchlistDto("my list", "2026-10-18T20:00:00.000000+00:00", ["A", "B"])));
        using var client = Client(stub);

        var lists = await client.GetWatchlistsAsync(TestContext.Current.CancellationToken);
        var one = await client.GetWatchlistAsync("my list", TestContext.Current.CancellationToken);

        Assert.Equal(["http://127.0.0.1:5280/v1/watchlists", "http://127.0.0.1:5280/v1/watchlists/my%20list"], stub.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal("favorites", lists.Watchlists[0].Name);
        Assert.Equal(["A", "B"], one.Markets);
    }

    [Fact]
    public async Task AWatchlistIsMadeWithAPostOfItsNameAndDeletedWithADelete()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.Created, Json(new WatchlistDto("mine", "2026-10-18T20:00:00.000000+00:00", [])))
            .Respond(HttpStatusCode.NoContent, string.Empty);
        using var client = Client(stub);

        var made = await client.CreateWatchlistAsync("mine", TestContext.Current.CancellationToken);
        await client.DeleteWatchlistAsync("mine", TestContext.Current.CancellationToken);

        Assert.Equal((HttpMethod.Post, "http://127.0.0.1:5280/v1/watchlists", """{"name":"mine"}"""), (stub.Requests[0].Method, stub.Requests[0].Uri.AbsoluteUri, stub.Requests[0].Body));
        Assert.Equal((HttpMethod.Delete, "http://127.0.0.1:5280/v1/watchlists/mine", null), (stub.Requests[1].Method, stub.Requests[1].Uri.AbsoluteUri, stub.Requests[1].Body));
        Assert.Equal("mine", made.Name);
    }

    [Fact]
    public async Task MarketsAreAddedWithAPutAndRemovedWithADeleteWithTheTickerEscaped()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.NoContent, string.Empty).Respond(HttpStatusCode.NoContent, string.Empty);
        using var client = Client(stub);

        await client.AddToWatchlistAsync("mine", "KX A/B", TestContext.Current.CancellationToken);
        await client.RemoveFromWatchlistAsync("mine", "KX A/B", TestContext.Current.CancellationToken);

        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], stub.Requests.Select(r => r.Method));
        Assert.All(stub.Requests, r => Assert.Equal("http://127.0.0.1:5280/v1/watchlists/mine/markets/KX%20A%2FB", r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task AnAlertIsMadeWithItsRequestAsJsonAndPatchedAndDeletedById()
    {
        var made = new AlertDto(8, "A", "price_above", "0.600000", 60, false, "2026-10-18T20:00:00.000000+00:00", null, null);
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.Created, Json(made))
            .Respond(HttpStatusCode.NoContent, string.Empty)
            .Respond(HttpStatusCode.NoContent, string.Empty);
        using var client = Client(stub);

        var got = await client.CreateAlertAsync(new CreateAlertRequest("A", "price_above", "0.6", 90), TestContext.Current.CancellationToken);
        await client.SetAlertPausedAsync(8, true, TestContext.Current.CancellationToken);
        await client.DeleteAlertAsync(8, TestContext.Current.CancellationToken);

        Assert.Equal((HttpMethod.Post, "http://127.0.0.1:5280/v1/alerts", """{"ticker":"A","kind":"price_above","threshold":"0.6","cooldown_seconds":90}"""), (stub.Requests[0].Method, stub.Requests[0].Uri.AbsoluteUri, stub.Requests[0].Body));
        Assert.Equal((HttpMethod.Patch, "http://127.0.0.1:5280/v1/alerts/8", """{"paused":true}"""), (stub.Requests[1].Method, stub.Requests[1].Uri.AbsoluteUri, stub.Requests[1].Body));
        Assert.Equal((HttpMethod.Delete, "http://127.0.0.1:5280/v1/alerts/8"), (stub.Requests[2].Method, stub.Requests[2].Uri.AbsoluteUri));
        Assert.Equal(8, got.Id);
    }

    [Fact]
    public async Task AlertsAndFiresAreReadWithGetAndTheFireCursorIsSent()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new AlertListDto([])))
            .Respond(HttpStatusCode.OK, Json(new AlertFiresPage([], null)))
            .Respond(HttpStatusCode.OK, Json(new AlertFiresPage([], null)));
        using var client = Client(stub);

        await client.GetAlertsAsync(TestContext.Current.CancellationToken);
        await client.GetAlertFiresAsync(cancellationToken: TestContext.Current.CancellationToken);
        await client.GetAlertFiresAsync(41, 5, TestContext.Current.CancellationToken);

        Assert.Equal(["http://127.0.0.1:5280/v1/alerts", "http://127.0.0.1:5280/v1/alerts/fires", "http://127.0.0.1:5280/v1/alerts/fires?after=41&limit=5"], stub.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task TheTimeZoneIsReadSetAndCleared()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new SettingDto("time_zone", "Asia/Tokyo")))
            .Respond(HttpStatusCode.OK, Json(new SettingDto("time_zone", "Europe/London")))
            .Respond(HttpStatusCode.NoContent, string.Empty);
        using var client = Client(stub);

        var read = await client.GetTimeZoneSettingAsync(TestContext.Current.CancellationToken);
        await client.SetTimeZoneSettingAsync("Europe/London", TestContext.Current.CancellationToken);
        await client.SetTimeZoneSettingAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("Asia/Tokyo", read.Value);
        Assert.Equal((HttpMethod.Put, "http://127.0.0.1:5280/v1/me/settings/time_zone", """{"value":"Europe/London"}"""), (stub.Requests[1].Method, stub.Requests[1].Uri.AbsoluteUri, stub.Requests[1].Body));
        Assert.Equal((HttpMethod.Delete, null), (stub.Requests[2].Method, stub.Requests[2].Body));
    }

    [Fact]
    public async Task TheScreenerCarriesTheWatchlistName()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Json(new ScreenerPage("volume", "desc", null, null, [], null)));
        using var client = Client(stub);

        await client.GetScreenerAsync(new ScreenQuery(Watchlist: "mine"), TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5280/v1/screener?watchlist=mine", stub.Requests[0].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "already_exists")]
    [InlineData(HttpStatusCode.Conflict, "limit_reached")]
    [InlineData(HttpStatusCode.NotFound, "market_not_found")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    public async Task ARefusedChangeBecomesAnApiExceptionWithTheServersCode(HttpStatusCode status, string code)
    {
        var stub = new StubHttpMessageHandler().Respond(status, Error(code));
        using var client = Client(stub);

        var thrown = await Assert.ThrowsAsync<ApiException>(() => client.CreateWatchlistAsync("mine", TestContext.Current.CancellationToken));

        Assert.Equal((status, code), (thrown.Status, thrown.Code));
    }

    [Fact]
    public async Task AnUnreachableServerOnAChangeIsAnApiUnavailableException()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("no route"));
        using var client = Client(stub);

        await Assert.ThrowsAsync<ApiUnavailableException>(() => client.DeleteAlertAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABrokenReplyToACreateIsABadResponseNotACrash()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.Created, "not json").Respond(HttpStatusCode.Created, "null");
        using var client = Client(stub);

        var first = await Assert.ThrowsAsync<ApiException>(() => client.CreateWatchlistAsync("mine", TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<ApiException>(() => client.CreateAlertAsync(new CreateAlertRequest("A", "price_above", "0.5"), TestContext.Current.CancellationToken));

        Assert.Equal(("bad_response", "bad_response"), (first.Code, second.Code));
    }

    [Fact]
    public async Task EveryRequestThatChangesSomethingGoesToTheCallersOwnDataAndNowhereElse()
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < 9; i++)
        {
            stub.Respond(HttpStatusCode.OK, "{}");   // a body the create calls cannot read: they report bad_response, which is not what is being looked at
        }

        using var client = Client(stub);
        var token = TestContext.Current.CancellationToken;
        await Swallow(client.CreateWatchlistAsync("a", token));
        await Swallow(client.DeleteWatchlistAsync("a", token));
        await Swallow(client.AddToWatchlistAsync("a", "T", token));
        await Swallow(client.RemoveFromWatchlistAsync("a", "T", token));
        await Swallow(client.CreateAlertAsync(new CreateAlertRequest("T", "price_above", "0.5"), token));
        await Swallow(client.SetAlertPausedAsync(1, true, token));
        await Swallow(client.DeleteAlertAsync(1, token));
        await Swallow(client.SetTimeZoneSettingAsync("UTC", token));
        await Swallow(client.SetTimeZoneSettingAsync(null, token));

        var writes = stub.Requests.Where(r => r.Method != HttpMethod.Get).ToList();
        Assert.Equal(9, writes.Count);
        Assert.All(writes, r => Assert.Matches("^/v1/(watchlists|alerts|me/settings)", r.Uri.AbsolutePath));
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (ApiException)
        {
            // Only the request matters here, not what the stub answered.
        }
    }
}
