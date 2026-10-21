using System.Net;
using KalTerm.Contracts;
using KalTerm.Data.Alerts;
using KalTerm.Data.Metrics;
using KalTerm.Data.Users;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

public class AlertTextTests
{
    [Theory]
    [InlineData(AlertKind.PriceAbove, "0.6", 600_000)]
    [InlineData(AlertKind.PriceBelow, "0.000001", 1)]
    [InlineData(AlertKind.SpreadAbove, "1", 1_000_000)]
    [InlineData(AlertKind.SpreadAbove, "0.05", 50_000)]
    [InlineData(AlertKind.VolumeSpike, "5", 5)]
    [InlineData(AlertKind.OiChange, "500", 50_000)]
    [InlineData(AlertKind.OiChange, "12.5", 1_250)]
    public void AThresholdIsReadInTheUnitOfItsKind(AlertKind kind, string text, long stored)
    {
        Assert.True(AlertText.TryParse(kind, text, out var parsed));
        Assert.Equal(stored, parsed);
    }

    [Theory]
    [InlineData(AlertKind.PriceAbove, "1")]               // a price of a whole dollar can never be reached from below
    [InlineData(AlertKind.PriceAbove, "0")]
    [InlineData(AlertKind.PriceAbove, "-0.5")]
    [InlineData(AlertKind.PriceAbove, "0.1234567")]       // more than six decimals
    [InlineData(AlertKind.PriceAbove, "1e-1")]
    [InlineData(AlertKind.PriceAbove, " 0.5")]
    [InlineData(AlertKind.PriceAbove, "")]
    [InlineData(AlertKind.PriceAbove, null)]
    [InlineData(AlertKind.VolumeSpike, "2.5")]            // a factor is whole
    [InlineData(AlertKind.VolumeSpike, "1")]
    [InlineData(AlertKind.VolumeSpike, "1001")]
    [InlineData(AlertKind.OiChange, "12.345")]            // contracts have two decimals
    [InlineData(AlertKind.OiChange, "0.5")]
    [InlineData(AlertKind.OiChange, "9999999999999")]
    public void AThresholdThatCannotBeExactlyAndSensiblyMeantIsRefused(AlertKind kind, string? text)
    {
        Assert.False(AlertText.TryParse(kind, text, out _));
    }

    [Fact]
    public void ThresholdsAndValuesAreWrittenInTheUnitOfTheirKind()
    {
        Assert.Equal("0.600000", AlertText.Format(AlertKind.PriceAbove, 600_000));
        Assert.Equal("5", AlertText.Format(AlertKind.VolumeSpike, 5));
        Assert.Equal("500.00", AlertText.Format(AlertKind.OiChange, 50_000));
        Assert.Equal("-12.50", AlertText.Format(AlertKind.OiChange, -1_250));
    }
}

/// <summary>The endpoints about the caller's own data, over a fake store, as a user the sign-in check would have named.</summary>
public sealed class UserEndpointTests : IAsyncLifetime
{
    private ApiTestHost _api = null!;

    private FakeReaders Fakes => _api.Fakes;

    public async ValueTask InitializeAsync() => _api = await ApiTestHost.StartAsUserAsync(42);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task AssertInvalidAsync(HttpMethod method, string url, string? json, params string[] fields)
    {
        var (response, body) = await _api.SendAsync(method, url, json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = ApiJson.Deserialize<ErrorDto>(body)!;
        Assert.Equal("invalid_parameter", error.Error);
        Assert.Equal(fields, error.Fields);
    }

    // ---- watchlists -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WatchlistsAreListedForTheCallerAndOnlyTheCaller()
    {
        Fakes.UserData.Lists = [new WatchlistSummary("favorites", 3, NowUtc)];

        var lists = await _api.GetAsync<WatchlistListDto>("/v1/watchlists");

        Assert.Equal(("favorites", 3, ApiFormat.Moment(NowUtc)), (lists.Watchlists[0].Name, lists.Watchlists[0].Markets, lists.Watchlists[0].CreatedAt));
        Assert.Equal(["list-watchlists 42"], Fakes.UserData.Calls);
    }

    [Fact]
    public async Task ACallerWithoutAUserGetsAnUnauthorizedAnswerForEveryUserEndpoint()
    {
        await using var anonymous = await ApiTestHost.StartAsync();

        foreach (var (method, url, json) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, "/v1/watchlists", null),
            (HttpMethod.Post, "/v1/watchlists", """{"name":"x"}"""),
            (HttpMethod.Get, "/v1/watchlists/x", null),
            (HttpMethod.Delete, "/v1/watchlists/x", null),
            (HttpMethod.Put, "/v1/watchlists/x/markets/KX-1", null),
            (HttpMethod.Delete, "/v1/watchlists/x/markets/KX-1", null),
            (HttpMethod.Get, "/v1/alerts", null),
            (HttpMethod.Post, "/v1/alerts", """{"ticker":"KX-1","kind":"price_above","threshold":"0.5"}"""),
            (HttpMethod.Patch, "/v1/alerts/1", """{"paused":true}"""),
            (HttpMethod.Delete, "/v1/alerts/1", null),
            (HttpMethod.Get, "/v1/alerts/fires", null),
            (HttpMethod.Get, "/v1/me/layouts", null),
            (HttpMethod.Get, "/v1/me/layouts/x", null),
            (HttpMethod.Put, "/v1/me/layouts/x", """{"version":1}"""),
            (HttpMethod.Delete, "/v1/me/layouts/x", null),
            (HttpMethod.Get, "/v1/me/settings/time_zone", null),
            (HttpMethod.Put, "/v1/me/settings/time_zone", """{"value":"UTC"}"""),
            (HttpMethod.Delete, "/v1/me/settings/time_zone", null),
        })
        {
            var (response, body) = await anonymous.SendAsync(method, url, json);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} gave {(int)response.StatusCode}: {body}");
        }

        Assert.Empty(anonymous.Fakes.UserData.Calls);
    }

    [Fact]
    public async Task AWatchlistIsMadeWithA201AndRefusedWhenItExistsOrTheCallerIsFull()
    {
        var (made, body) = await _api.SendAsync(HttpMethod.Post, "/v1/watchlists", """{"name":"favorites"}""");
        Fakes.UserData.Outcome = StoreOutcome.Exists;
        var (taken, takenBody) = await _api.SendAsync(HttpMethod.Post, "/v1/watchlists", """{"name":"favorites"}""");
        Fakes.UserData.Outcome = StoreOutcome.LimitReached;
        var (full, fullBody) = await _api.SendAsync(HttpMethod.Post, "/v1/watchlists", """{"name":"another"}""");

        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(("favorites", 0), (ApiJson.Deserialize<WatchlistDto>(body)!.Name, ApiJson.Deserialize<WatchlistDto>(body)!.Markets.Count));
        Assert.Equal((HttpStatusCode.Conflict, "already_exists"), (taken.StatusCode, ApiJson.Deserialize<ErrorDto>(takenBody)!.Error));
        Assert.Equal((HttpStatusCode.Conflict, "limit_reached"), (full.StatusCode, ApiJson.Deserialize<ErrorDto>(fullBody)!.Error));
    }

    [Theory]
    [InlineData("""{"name":"Has Space"}""")]
    [InlineData("""{"name":"UPPER"}""")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"-leading-dash"}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task AWatchlistNeedsAnUsableNameInAJsonBody(string json)
    {
        await AssertInvalidAsync(HttpMethod.Post, "/v1/watchlists", json, "name");

        Assert.Empty(Fakes.UserData.Calls);
    }

    [Fact]
    public async Task ATooLongBodyIsRefusedWithoutBeingRead()
    {
        var json = "{\"name\":\"" + new string('a', 5_000) + "\"}";

        await AssertInvalidAsync(HttpMethod.Post, "/v1/watchlists", json, "name");
    }

    [Fact]
    public async Task OneWatchlistShowsItsMarketsOrIsNotFound()
    {
        Fakes.UserData.Detail = new WatchlistDetail("favorites", NowUtc, ["KX-1", "KX-2"]);

        var found = await _api.GetAsync<WatchlistDto>("/v1/watchlists/favorites");
        Fakes.UserData.Detail = null;
        var (missing, _) = await _api.GetAsync("/v1/watchlists/nothing");

        Assert.Equal(["KX-1", "KX-2"], found.Markets);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertInvalidAsync(HttpMethod.Get, "/v1/watchlists/Bad%20Name", null, "name");
    }

    [Fact]
    public async Task AWatchlistIsDeletedWith204OrIsNotFound()
    {
        var (gone, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/watchlists/favorites");
        Fakes.UserData.Outcome = StoreOutcome.NotFound;
        var (missing, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/watchlists/favorites");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (gone.StatusCode, missing.StatusCode));
        Assert.Equal(["delete-watchlist 42 favorites", "delete-watchlist 42 favorites"], Fakes.UserData.Calls);
    }

    [Fact]
    public async Task MarketsAreAddedAndRemovedAndEachFailureSaysWhatWasMissing()
    {
        var (added, _) = await _api.SendAsync(HttpMethod.Put, "/v1/watchlists/favorites/markets/KX-1");
        var (removed, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/watchlists/favorites/markets/KX-1");
        Fakes.UserData.Outcome = StoreOutcome.MarketNotFound;
        var (noMarket, noMarketBody) = await _api.SendAsync(HttpMethod.Put, "/v1/watchlists/favorites/markets/KX-NOPE");
        Fakes.UserData.Outcome = StoreOutcome.NotFound;
        var (noList, noListBody) = await _api.SendAsync(HttpMethod.Put, "/v1/watchlists/nothing/markets/KX-1");
        Fakes.UserData.Outcome = StoreOutcome.LimitReached;
        var (full, fullBody) = await _api.SendAsync(HttpMethod.Put, "/v1/watchlists/favorites/markets/KX-1");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (added.StatusCode, removed.StatusCode));
        Assert.Equal((HttpStatusCode.NotFound, "market_not_found"), (noMarket.StatusCode, ApiJson.Deserialize<ErrorDto>(noMarketBody)!.Error));
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), (noList.StatusCode, ApiJson.Deserialize<ErrorDto>(noListBody)!.Error));
        Assert.Equal((HttpStatusCode.Conflict, "limit_reached"), (full.StatusCode, ApiJson.Deserialize<ErrorDto>(fullBody)!.Error));
        await AssertInvalidAsync(HttpMethod.Put, "/v1/watchlists/Bad%20Name/markets/bad%20ticker", null, "name", "ticker");
        await AssertInvalidAsync(HttpMethod.Delete, "/v1/watchlists/favorites/markets/bad%20ticker", null, "ticker");
    }

    [Fact]
    public async Task TheScreenerCanBeRestrictedToOneOfTheCallersWatchlists()
    {
        Fakes.UserData.MarketIds = [11, 12];

        await _api.GetAsync<ScreenerPage>("/v1/screener?watchlist=favorites");

        Assert.Equal(["market-ids 42 favorites"], Fakes.UserData.Calls);
        Assert.Equal([11, 12], Fakes.Screener.Queries.Single().OnlyMarkets);
    }

    [Fact]
    public async Task AScreenerWatchlistThatDoesNotExistIsNotFoundAndABadNameIsRefused()
    {
        Fakes.UserData.MarketIds = null;

        var (response, _) = await _api.GetAsync("/v1/screener?watchlist=nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Fakes.Screener.Queries);
        var (bad, body) = await _api.GetAsync("/v1/screener?watchlist=Bad%20Name");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(["watchlist"], ApiJson.Deserialize<ErrorDto>(body)!.Fields);
    }

    [Fact]
    public async Task TheScreenerWithAWatchlistNeedsAUser()
    {
        await using var anonymous = await ApiTestHost.StartAsync();

        var (response, _) = await anonymous.GetAsync("/v1/screener?watchlist=favorites");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- alerts ---------------------------------------------------------------------------------------------------------------------

    private static UserAlert Alert(int id, AlertKind kind = AlertKind.PriceAbove, long threshold = 600_000) =>
        new(id, "KX-1", kind, threshold, 60, false, NowUtc, null, null);

    [Fact]
    public async Task AlertsAreListedInTheUnitOfTheirKind()
    {
        Fakes.UserData.Alerts = [Alert(1), Alert(2, AlertKind.VolumeSpike, 5), Alert(3, AlertKind.OiChange, 50_000)];

        var alerts = await _api.GetAsync<AlertListDto>("/v1/alerts");

        Assert.Equal(["0.600000", "5", "500.00"], alerts.Alerts.Select(a => a.Threshold));
        Assert.Equal(["price_above", "volume_spike", "oi_change"], alerts.Alerts.Select(a => a.Kind));
        Assert.Equal((60, false, ApiFormat.Moment(NowUtc), null, null), (alerts.Alerts[0].CooldownSeconds, alerts.Alerts[0].Paused, alerts.Alerts[0].CreatedAt, alerts.Alerts[0].LastEvaluatedAt, alerts.Alerts[0].LastFiredAt));
    }

    [Fact]
    public async Task AnAlertIsMadeWithA201TheDefaultCooldownAndTheThresholdInStoredUnits()
    {
        var (response, body) = await _api.SendAsync(HttpMethod.Post, "/v1/alerts", """{"ticker":"KX-1","kind":"price_above","threshold":"0.60"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["add-alert 42 KX-1 price_above 600000 60", "list-alerts 42"], Fakes.UserData.Calls);
        var made = ApiJson.Deserialize<AlertDto>(body)!;
        Assert.Equal((5, "KX-1", "price_above", "0.600000", 60), (made.Id, made.Ticker, made.Kind, made.Threshold, made.CooldownSeconds));
    }

    [Fact]
    public async Task ACooldownOfTheCallersChoosingIsPassedOn()
    {
        await _api.SendAsync(HttpMethod.Post, "/v1/alerts", """{"ticker":"KX-1","kind":"oi_change","threshold":"500","cooldown_seconds":300}""");

        Assert.Equal("add-alert 42 KX-1 oi_change 50000 300", Fakes.UserData.Calls[0]);
    }

    [Theory]
    [InlineData("""{"ticker":"bad ticker","kind":"price_above","threshold":"0.5"}""", "ticker")]
    [InlineData("""{"ticker":"KX-1","kind":"price_sideways","threshold":"0.5"}""", "kind")]
    [InlineData("""{"ticker":"KX-1","kind":"price_above","threshold":"1.5"}""", "threshold")]
    [InlineData("""{"ticker":"KX-1","kind":"price_above","threshold":"abc"}""", "threshold")]
    [InlineData("""{"ticker":"KX-1","kind":"volume_spike","threshold":"1.5"}""", "threshold")]
    [InlineData("""{"ticker":"KX-1","kind":"price_above","threshold":"0.5","cooldown_seconds":5}""", "cooldown_seconds")]
    [InlineData("""{"ticker":"KX-1","kind":"price_above","threshold":"0.5","cooldown_seconds":86401}""", "cooldown_seconds")]
    [InlineData("""{"ticker":"bad ticker","kind":"nope","threshold":"x","cooldown_seconds":1}""", "ticker,kind,cooldown_seconds")]
    public async Task AnAlertWithAnUnusableFieldIsRefusedNamingIt(string json, string fields)
    {
        await AssertInvalidAsync(HttpMethod.Post, "/v1/alerts", json, fields.Split(','));

        Assert.Empty(Fakes.UserData.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task AnAlertNeedsAJsonObjectBody(string json)
    {
        var (response, body) = await _api.SendAsync(HttpMethod.Post, "/v1/alerts", json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["body"], ApiJson.Deserialize<ErrorDto>(body)!.Fields);
    }

    [Fact]
    public async Task AnAlertOnAnUnknownMarketIsNotFoundAndAFullCallerIsTurnedAway()
    {
        Fakes.UserData.Outcome = StoreOutcome.MarketNotFound;
        var (nope, nopeBody) = await _api.SendAsync(HttpMethod.Post, "/v1/alerts", """{"ticker":"KX-NOPE","kind":"price_above","threshold":"0.5"}""");
        Fakes.UserData.Outcome = StoreOutcome.LimitReached;
        var (full, fullBody) = await _api.SendAsync(HttpMethod.Post, "/v1/alerts", """{"ticker":"KX-1","kind":"price_above","threshold":"0.5"}""");

        Assert.Equal((HttpStatusCode.NotFound, "market_not_found"), (nope.StatusCode, ApiJson.Deserialize<ErrorDto>(nopeBody)!.Error));
        Assert.Equal((HttpStatusCode.Conflict, "limit_reached"), (full.StatusCode, ApiJson.Deserialize<ErrorDto>(fullBody)!.Error));
    }

    [Fact]
    public async Task AnAlertIsPausedResumedAndDeletedByIdAndAnotherUsersIdIsNotFound()
    {
        var (paused, _) = await _api.SendAsync(HttpMethod.Patch, "/v1/alerts/7", """{"paused":true}""");
        var (resumed, _) = await _api.SendAsync(HttpMethod.Patch, "/v1/alerts/7", """{"paused":false}""");
        var (deleted, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/alerts/7");
        Fakes.UserData.Outcome = StoreOutcome.NotFound;
        var (foreign, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/alerts/99");

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NotFound], [paused.StatusCode, resumed.StatusCode, deleted.StatusCode, foreign.StatusCode]);
        Assert.Equal(["pause-alert 42 7 True", "pause-alert 42 7 False", "delete-alert 42 7", "delete-alert 42 99"], Fakes.UserData.Calls);
    }

    [Fact]
    public async Task AnAlertIdMustBeANumberAndAPatchNeedsItsBody()
    {
        await AssertInvalidAsync(HttpMethod.Delete, "/v1/alerts/seven", null, "id");
        await AssertInvalidAsync(HttpMethod.Patch, "/v1/alerts/seven", """{"paused":true}""", "id");
        await AssertInvalidAsync(HttpMethod.Patch, "/v1/alerts/7", "nope", "body");
    }

    [Fact]
    public async Task FiresComeInTheUnitOfTheirKindWithTheLastIdToAskFrom()
    {
        Fakes.UserData.Fires = [new UserFire(41, 7, "KX-1", AlertKind.PriceAbove, 600_000, 610_000, NowUtc), new UserFire(43, 8, "KX-2", AlertKind.OiChange, 50_000, -75_000, NowUtc)];

        var page = await _api.GetAsync<AlertFiresPage>("/v1/alerts/fires?after=40&limit=7");

        Assert.Equal(43L, page.LastId);
        Assert.Equal(["0.610000", "-750.00"], page.Fires.Select(f => f.Value));
        Assert.Equal(["0.600000", "500.00"], page.Fires.Select(f => f.Threshold));
        Assert.Equal(["fires 42 40 7"], Fakes.UserData.Calls);
    }

    [Fact]
    public async Task WithoutFiresThereIsNoLastIdAndTheDefaultIsTheNewestFifty()
    {
        var page = await _api.GetAsync<AlertFiresPage>("/v1/alerts/fires");

        Assert.Null(page.LastId);
        Assert.Equal(["fires 42  50"], Fakes.UserData.Calls);
    }

    [Theory]
    [InlineData("after=abc", "after")]
    [InlineData("after=-1", "after")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=1001", "limit")]
    public async Task BadFireParametersAreNamedAndRefused(string query, string field)
    {
        await AssertInvalidAsync(HttpMethod.Get, "/v1/alerts/fires?" + query, null, field);
    }

    // ---- settings -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStoredTimeZoneIsReadSetAndCleared()
    {
        Fakes.UserData.Setting = "Asia/Tokyo";
        var read = await _api.GetAsync<SettingDto>("/v1/me/settings/time_zone");
        var (set, setBody) = await _api.SendAsync(HttpMethod.Put, "/v1/me/settings/time_zone", """{"value":"Europe/London"}""");
        var (cleared, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/me/settings/time_zone");

        Assert.Equal(("time_zone", "Asia/Tokyo"), (read.Name, read.Value));
        Assert.Equal(("Europe/London", HttpStatusCode.NoContent), (ApiJson.Deserialize<SettingDto>(setBody)!.Value, cleared.StatusCode));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(["get-setting 42 time_zone", "set-setting 42 time_zone Europe/London", "set-setting 42 time_zone (clear)"], Fakes.UserData.Calls);
    }

    [Theory]
    [InlineData("""{"value":"Mars/Olympus"}""")]
    [InlineData("""{"value":"has space"}""")]
    [InlineData("""{"value":""}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{}""")]
    [InlineData("nope")]
    public async Task ATimeZoneTheServerDoesNotKnowIsNeverStored(string json)
    {
        await AssertInvalidAsync(HttpMethod.Put, "/v1/me/settings/time_zone", json, "value");

        Assert.Empty(Fakes.UserData.Calls);
    }

    [Fact]
    public async Task LayoutsAreListedAndOneIsReadWithItsBodyAsJson()
    {
        Fakes.UserData.LayoutList = [new LayoutSummary("desk", NowUtc), new LayoutSummary("laptop", NowUtc)];
        Fakes.UserData.Layout = new LayoutDetail("desk", NowUtc, """{"version":1,"window":{"width":1100}}""");

        var list = await _api.GetAsync<LayoutListDto>("/v1/me/layouts");
        var one = await _api.GetAsync<LayoutDto>("/v1/me/layouts/desk");

        Assert.Equal(["desk", "laptop"], list.Layouts.Select(l => l.Name));
        Assert.Equal(1100, one.Body.GetProperty("window").GetProperty("width").GetInt32());
        Assert.Contains(Fakes.UserData.Calls, c => c.StartsWith("get-layout ", StringComparison.Ordinal) && c.EndsWith(" desk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingLayoutIsNotFoundAndABadNameIsRefused()
    {
        Fakes.UserData.Layout = null;

        var (missing, _) = await _api.GetAsync("/v1/me/layouts/nothing");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertInvalidAsync(HttpMethod.Get, "/v1/me/layouts/Bad%20Name", null, "name");
        await AssertInvalidAsync(HttpMethod.Put, "/v1/me/layouts/Bad%20Name", "{}", "name");
        await AssertInvalidAsync(HttpMethod.Delete, "/v1/me/layouts/Bad%20Name", null, "name");
    }

    [Fact]
    public async Task ALayoutIsSavedAsItWasSentAndTheLimitIsAConflict()
    {
        var (saved, savedBody) = await _api.SendAsync(HttpMethod.Put, "/v1/me/layouts/desk", """{"version":1,"page":"Markets"}""");
        Fakes.UserData.Outcome = StoreOutcome.LimitReached;
        var (full, fullBody) = await _api.SendAsync(HttpMethod.Put, "/v1/me/layouts/another", """{"version":1}""");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Markets", ApiJson.Deserialize<LayoutDto>(savedBody)!.Body.GetProperty("page").GetString());
        Assert.Contains(Fakes.UserData.Calls, c => c.EndsWith(" desk {\"version\":1,\"page\":\"Markets\"}", StringComparison.Ordinal));
        Assert.Equal((HttpStatusCode.Conflict, "limit_reached"), (full.StatusCode, ApiJson.Deserialize<ErrorDto>(fullBody)!.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task ALayoutBodyMustBeAJsonObject(string json)
    {
        await AssertInvalidAsync(HttpMethod.Put, "/v1/me/layouts/desk", json, "body");

        Assert.DoesNotContain(Fakes.UserData.Calls, c => c.StartsWith("put-layout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALayoutBodyOverThirtyTwoKilobytesIsRefusedAndOneJustUnderIsKept()
    {
        var big = "{\"x\":\"" + new string('a', 33_000) + "\"}";
        var fits = "{\"x\":\"" + new string('a', 30_000) + "\"}";

        await AssertInvalidAsync(HttpMethod.Put, "/v1/me/layouts/desk", big, "body");
        var (kept, _) = await _api.SendAsync(HttpMethod.Put, "/v1/me/layouts/desk", fits);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task ALayoutIsDeletedWith204OrIsNotFound()
    {
        var (gone, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/me/layouts/desk");
        Fakes.UserData.Outcome = StoreOutcome.NotFound;
        var (missing, _) = await _api.SendAsync(HttpMethod.Delete, "/v1/me/layouts/desk");

        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void ZonesAreCheckedAgainstWhatThisComputerKnows()
    {
        Assert.True(UserEndpoints.KnownZone("UTC"));
        Assert.True(UserEndpoints.KnownZone("America/Denver"));
        Assert.False(UserEndpoints.KnownZone("America/Nowhere"));
        Assert.False(UserEndpoints.KnownZone(new string('a', 65)));
    }
}
