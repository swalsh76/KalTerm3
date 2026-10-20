using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.Cli.Tests;

/// <summary>Watchlists, alerts and the stored time zone from the command line.</summary>
public sealed class UserCommandTests : IDisposable
{
    private readonly Harness _cli = new();

    public void Dispose() => _cli.Dispose();

    // ---- watchlists -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WatchlistsAreListedWithTheirSizes()
    {
        var exit = await _cli.RunAsync("watchlist", "list");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("favorites", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("1 watchlist(s)", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneWatchlistShowsItsMarkets()
    {
        await _cli.RunAsync("watchlist", "show", "favorites");

        Assert.Contains("2 market(s)", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("  A-1", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("  B-2", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyWatchlistSaysSo()
    {
        _cli.Api.Watchlist = new WatchlistDto("empty", "2026-10-18T20:00:00.000000+00:00", []);

        await _cli.RunAsync("watchlist", "show", "empty");

        Assert.Contains("(empty)", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingAddingRemovingAndDeletingGoToTheApiOneByOne()
    {
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("watchlist", "create", "mine"));
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("watchlist", "add", "mine", "A-1", "B-2"));
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("watchlist", "remove", "mine", "A-1"));
        Assert.Equal(CliApp.Ok, await _cli.RunAsync("watchlist", "delete", "mine"));

        Assert.Equal(["create-watchlist mine", "add mine A-1", "add mine B-2", "remove mine A-1", "delete-watchlist mine"], _cli.Api.Changes);
        Assert.Contains("created watchlist mine", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiRefusalOfAWatchlistChangeIsReportedWithItsCode()
    {
        _cli.Api.Throw = new ApiException(HttpStatusCode.Conflict, "already_exists");

        var exit = await _cli.RunAsync("watchlist", "create", "mine");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("already_exists", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScreenerCanBeRestrictedToAWatchlist()
    {
        await _cli.RunAsync("screen", "--watchlist", "favorites");

        Assert.Equal("favorites", _cli.Api.ScreenQueries.Single().Watchlist);
    }

    // ---- alerts ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AlertsAreListedWithTheirKindThresholdAndState()
    {
        _cli.Api.Alerts = new AlertListDto([
            new AlertDto(7, "A-1", "price_above", "0.600000", 60, false, "2026-10-18T20:00:00.000000+00:00", "2026-10-18T22:00:00.000000+00:00", null),
            new AlertDto(9, "B-2", "oi_change", "500.00", 120, true, "2026-10-18T20:00:00.000000+00:00", null, "2026-10-18T21:00:00.000000+00:00")]);

        await _cli.RunAsync("alert", "list");

        Assert.Contains("price_above", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.600000", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("paused", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 21:00:00", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2 alert(s)", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakingAnAlertSendsTheTextAsTypedAndShowsTheNewId()
    {
        var exit = await _cli.RunAsync("alert", "add", "A-1", "volume_spike", "5", "--cooldown", "30");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(["create-alert A-1 volume_spike 5 30"], _cli.Api.Changes);
        Assert.Contains("alert 8: A-1 volume_spike 5", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlertWithoutACooldownLeavesItToTheServer()
    {
        await _cli.RunAsync("alert", "add", "A-1", "price_above", "0.60");

        Assert.Equal(["create-alert A-1 price_above 0.60 "], _cli.Api.Changes);
    }

    [Fact]
    public async Task PausingResumingAndRemovingAnAlert()
    {
        await _cli.RunAsync("alert", "pause", "7");
        await _cli.RunAsync("alert", "resume", "7");
        await _cli.RunAsync("alert", "remove", "7");

        Assert.Equal(["pause 7", "resume 7", "delete-alert 7"], _cli.Api.Changes);
        Assert.Contains("paused alert 7", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("resumed alert 7", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlertIdThatIsNotANumberIsRefusedBeforeAnyCall()
    {
        var exit = await _cli.RunAsync("alert", "pause", "seven");

        Assert.NotEqual(CliApp.Ok, exit);
        Assert.Empty(_cli.Api.Changes);
    }

    [Fact]
    public async Task FiresAreListedWithTheirValuesAndSayWhereToContinue()
    {
        var exit = await _cli.RunAsync("alert", "fires", "--after", "40", "--top", "5");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(["fires 40 5"], _cli.Api.Changes);
        Assert.Contains("0.610000", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 22:59:00.250", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("Newer ones: --after 41", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutFiresThereIsNoContinuationHint()
    {
        _cli.Api.Fires = new AlertFiresPage([], null);

        await _cli.RunAsync("alert", "fires");

        Assert.Contains("0 shown.", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--after", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonConfirmationsAreOkTrue()
    {
        await _cli.RunAsync("alert", "remove", "7", "--json");

        Assert.Equal("{\"ok\":true}", _cli.Output.Trim());
    }

    // ---- the stored time zone -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStoredZoneIsShownSetAndCleared()
    {
        await _cli.RunAsync("settings", "tz");
        Assert.Contains("no time zone stored", _cli.Output, StringComparison.Ordinal);

        await _cli.RunAsync("settings", "tz", "America/Denver");
        await _cli.RunAsync("settings", "tz", "--clear");

        Assert.Equal(["tz America/Denver", "tz (clear)"], _cli.Api.Changes);
    }

    [Fact]
    public async Task AStoredZoneIsUsedWhenNothingElseIsChosen()
    {
        _cli.Api.StoredTimeZone = "Asia/Tokyo";

        await _cli.RunAsync("status");

        Assert.Contains("Server status 2026-10-19 08:00:00 +09:00", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryExplicitChoiceBeatsTheStoredZone()
    {
        _cli.Api.StoredTimeZone = "Asia/Tokyo";
        using var withVariable = new Harness(new Dictionary<string, string> { [CliEnvironment.TimeZoneVariable] = "Europe/London" }) { };
        withVariable.Api.StoredTimeZone = "Asia/Tokyo";

        await _cli.RunAsync("status", "--tz", "America/Denver");
        await withVariable.RunAsync("status");

        Assert.Contains("17:00:00 -06:00", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("00:00:00 +01:00", withVariable.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATokenTheStoredZoneIsNotAskedForAndAnUnknownStoredZoneFallsBack()
    {
        _cli.HasStoredToken = false;
        _cli.Api.StoredTimeZone = "Asia/Tokyo";
        await _cli.RunAsync("status");
        using var odd = new Harness();
        odd.Api.StoredTimeZone = "Mars/Olympus";
        await odd.RunAsync("status");

        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", odd.Output, StringComparison.Ordinal);
    }
}
