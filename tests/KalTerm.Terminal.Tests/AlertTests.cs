using System.Net;
using System.Windows.Controls;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

internal static class AlertSamples
{
    public static AlertFireDto Fire(long id, string ticker = "KX-A", string kind = "price_above", string threshold = "0.600000", string value = "0.610000", int alertId = 1) =>
        new(id, alertId, ticker, kind, threshold, value, "2026-10-20T20:02:11.000000+00:00");

    public static AlertDto Alert(int id, string ticker = "KX-A", string kind = "price_above", string threshold = "0.600000", int cooldown = 60, bool paused = false, string? lastFired = null) =>
        new(id, ticker, kind, threshold, cooldown, paused, "2026-10-20T00:00:00+00:00", null, lastFired);
}

public sealed class AlertTextTests
{
    [Theory]
    [InlineData("price_above", "price above")]
    [InlineData("price_below", "price below")]
    [InlineData("spread_above", "spread above")]
    [InlineData("volume_spike", "volume spike")]
    [InlineData("oi_change", "open interest change")]
    [InlineData("something_new", "something_new")]
    public void KindsAreInWords(string kind, string words) => Assert.Equal(words, AlertText.Kind(kind));

    [Theory]
    [InlineData("price_above", "0.600000", "0.60")]
    [InlineData("spread_above", "0.050000", "0.05")]
    [InlineData("volume_spike", "5", "×5")]
    [InlineData("volume_spike", "7.250000", "×7.25")]
    [InlineData("volume_spike", "lots", "lots")]
    [InlineData("oi_change", "1500.00", "1,500")]
    public void AmountsAreInTheUnitOfTheirKind(string kind, string amount, string shown) => Assert.Equal(shown, AlertText.Amount(kind, amount));

    [Fact]
    public void AConditionJoinsTheKindAndTheThreshold() => Assert.Equal("price below 0.40", AlertText.Condition("price_below", "0.400000"));
}

public sealed class AlertKindChoiceTests
{
    private static AlertKindChoice Kind(string key) => AlertKindChoice.All.Single(k => k.Key == key);

    [Theory]
    [InlineData("price_above", "0.60", true)]
    [InlineData("price_above", ".6", true)]
    [InlineData("price_above", "0.999999", true)]
    [InlineData("price_above", "1", false)]
    [InlineData("price_above", "0", false)]
    [InlineData("price_above", "0.1234567", false)]
    [InlineData("price_above", "abc", false)]
    [InlineData("price_below", "0.01", true)]
    [InlineData("spread_above", "1", true)]
    [InlineData("spread_above", "1.01", false)]
    [InlineData("spread_above", "0.05", true)]
    [InlineData("volume_spike", "2", true)]
    [InlineData("volume_spike", "1000", true)]
    [InlineData("volume_spike", "1", false)]
    [InlineData("volume_spike", "1001", false)]
    [InlineData("volume_spike", "2.5", false)]
    [InlineData("oi_change", "500", true)]
    [InlineData("oi_change", "1.5", true)]
    [InlineData("oi_change", "0.99", false)]
    [InlineData("oi_change", "1.234", false)]
    public void AThresholdIsJudgedInTheUnitOfItsKind(string key, string text, bool usable) => Assert.Equal(usable, Kind(key).Problem(text) is null);

    [Fact]
    public void AnEmptyThresholdAsksForOneAndAnUnusableOneSaysWhatWouldDo()
    {
        Assert.Equal("Type a threshold.", Kind("price_above").Problem("  "));
        Assert.Equal(Kind("price_above").Hint, Kind("price_above").Problem("5"));
        Assert.Equal(["price above", "price below", "spread above", "volume spike", "open interest change"], AlertKindChoice.All.Select(k => k.Text));
        Assert.Equal(["dollars", "dollars", "dollars", "times", "contracts"], AlertKindChoice.All.Select(k => k.Unit));
    }
}

public sealed class AlertServiceTests : IDisposable
{
    private readonly FakeApi _api = new();
    private readonly AlertService _service = new();
    private int _changes;

    public AlertServiceTests()
    {
        _api.AlertRows.AddRange([AlertSamples.Alert(1), AlertSamples.Alert(2, "KX-B", paused: true)]);
        _api.FireRows.AddRange([AlertSamples.Fire(1), AlertSamples.Fire(2), AlertSamples.Fire(3, alertId: 2)]);
        _service.Changed += () => _changes++;
        _service.Attach(_api);
    }

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task LoadingReadsTheAlertsAndTheLatestFiresNewestFirst()
    {
        Assert.False(_service.Loaded);

        Assert.Null(await _service.LoadAsync());

        Assert.True(_service.Loaded);
        Assert.Equal([1, 2], _service.Alerts.Select(a => a.Id));
        Assert.Equal([3L, 2L, 1L], _service.Fires.Select(f => f.Id));
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task LoadingWithoutAServerDoesNothingAndAFailureIsAnswerInWords()
    {
        Assert.Null(await new AlertService().LoadAsync());

        _api.AlertFails = new ApiUnavailableException("no route");
        Assert.Equal("Could not read your alerts: no route", await _service.LoadAsync());
        _api.AlertFails = Samples.Unauthorized();
        Assert.Equal("The server did not accept the token.", await _service.LoadAsync());
        Assert.False(_service.Loaded);
    }

    [Fact]
    public async Task AnAlertIsMadeWhenTheServerAcceptsIt()
    {
        _ = await _service.LoadAsync();
        _changes = 0;

        Assert.Null(await _service.CreateAsync(new CreateAlertRequest("KX-C", "oi_change", "500", 120)));

        Assert.Equal(3, _service.Alerts.Count);
        Assert.Equal(("KX-C", 120), (_service.Alerts[2].Ticker, _service.Alerts[2].CooldownSeconds));
        Assert.Equal(1, _changes);
        Assert.Contains("create KX-C oi_change 500 120", _api.AlertCalls);
    }

    [Fact]
    public async Task TheServersRefusalsToMakeAnAlertAreAnsweredInPlainWords()
    {
        _ = await _service.LoadAsync();
        var request = new CreateAlertRequest("KX-C", "price_above", "0.6");

        _api.AlertFails = new ApiException(HttpStatusCode.NotFound, "market_not_found");
        Assert.Equal("The server does not know a market called KX-C.", await _service.CreateAsync(request));
        _api.AlertFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        Assert.Equal("You already have 200 alerts. Delete one first.", await _service.CreateAsync(request));
        _api.AlertFails = new ApiException(HttpStatusCode.BadRequest, "invalid_parameter", ["threshold"]);
        Assert.Equal("The server did not accept the alert (threshold).", await _service.CreateAsync(request));
        _api.AlertFails = new ApiException(HttpStatusCode.BadRequest, "invalid_parameter");
        Assert.Equal("The server did not accept the alert.", await _service.CreateAsync(request));
        _api.AlertFails = new ApiUnavailableException("down");
        Assert.Equal("Could not make the alert: down", await _service.CreateAsync(request));

        Assert.Equal(2, _service.Alerts.Count);
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.CreateAsync(null!));
    }

    [Fact]
    public async Task AlertsArePausedResumedAndDeletedAndTheMemoryFollowsTheServer()
    {
        _ = await _service.LoadAsync();

        Assert.Null(await _service.SetPausedAsync(1, paused: true));
        Assert.True(_service.Alerts[0].Paused);
        Assert.Null(await _service.SetPausedAsync(2, paused: false));
        Assert.False(_service.Alerts[1].Paused);
        Assert.Null(await _service.DeleteAsync(1));

        Assert.Equal([2], _service.Alerts.Select(a => a.Id));
    }

    [Fact]
    public async Task ChangesToAnAlertThatIsGoneSayAndForgetItAndOtherFailuresAreInWords()
    {
        _ = await _service.LoadAsync();

        _api.AlertFails = new ApiException(HttpStatusCode.NotFound, "not_found");
        Assert.Equal("That alert no longer exists.", await _service.DeleteAsync(1));
        Assert.Equal([2], _service.Alerts.Select(a => a.Id));

        _api.AlertFails = new ApiUnavailableException("down");
        Assert.Equal("Could not pause the alert: down", await _service.SetPausedAsync(2, paused: true));
        _api.AlertFails = new ApiUnavailableException("down");
        Assert.Equal("Could not resume the alert: down", await _service.SetPausedAsync(2, paused: false));
        Assert.True(_service.Alerts[0].Paused);

        var detached = new AlertService();
        Assert.Equal("Not connected.", await detached.CreateAsync(new CreateAlertRequest("KX-A", "price_above", "0.6")));
        Assert.Equal("Not connected.", await detached.DeleteAsync(1));
    }

    [Fact]
    public async Task AFireThatArrivesGoesFirstOnceAndMarksItsAlert()
    {
        _ = await _service.LoadAsync();
        _changes = 0;

        _service.AddFire(AlertSamples.Fire(4));
        _service.AddFire(AlertSamples.Fire(4));
        _service.AddFire(AlertSamples.Fire(5, alertId: 99));

        Assert.Equal([5L, 4L, 3L, 2L, 1L], _service.Fires.Select(f => f.Id));
        Assert.Equal("2026-10-20T20:02:11.000000+00:00", _service.Alerts[0].LastFiredAt);
        Assert.Equal(2, _changes);
        Assert.Throws<ArgumentNullException>(() => _service.AddFire(null!));
    }

    [Fact]
    public void OnlyTheLatestHundredFiresAreKept()
    {
        for (var id = 1; id <= 105; id++)
        {
            _service.AddFire(AlertSamples.Fire(id));
        }

        Assert.Equal(AlertService.MaxFires, _service.Fires.Count);
        Assert.Equal(105, _service.Fires[0].Id);
        Assert.Equal(6, _service.Fires[^1].Id);
    }

    [Fact]
    public async Task DetachingForgetsEverythingAndSaysSoOnlyOnce()
    {
        _ = await _service.LoadAsync();
        _changes = 0;

        _service.Attach(null);
        _service.Attach(null);

        Assert.Empty(_service.Alerts);
        Assert.Empty(_service.Fires);
        Assert.False(_service.Loaded);
        Assert.Equal(1, _changes);
    }
}

public sealed class AlertSettingsTests : IDisposable
{
    private readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void TheNewestFireSeenIsRememberedForEachServerAndTheServerIsKept()
    {
        Assert.Null(_t.Settings.LoadLastFire("kaldb:5280"));
        _t.Settings.SaveServer(new Uri("https://kaldb:5280"));

        _t.Settings.SaveLastFire("kaldb:5280", 41);
        _t.Settings.SaveLastFire("other:5280", 7);
        _t.Settings.SaveLastFire("kaldb:5280", 42);
        _t.Settings.SaveServer(new Uri("https://kaldb:5280"));

        Assert.Equal((42L, 7L), (_t.Settings.LoadLastFire("kaldb:5280"), _t.Settings.LoadLastFire("other:5280")));
        Assert.Equal(new Uri("https://kaldb:5280"), _t.Settings.LoadServer());
        Assert.Null(_t.Settings.LoadLastFire("third:5280"));
    }

    [Fact]
    public void AFileThatCannotBeReadIsForgotten()
    {
        var path = Path.Combine(Path.GetTempPath(), "kalterm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var store = new TerminalSettingsStore(path);
            Assert.Null(store.LoadServer());
            Assert.Null(store.LoadLastFire("kaldb:5280"));
            store.SaveLastFire("kaldb:5280", 3);
            Assert.Equal(3, store.LoadLastFire("kaldb:5280"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class AlertNotifierTests : IDisposable
{
    private const string Key = "kaldb:5280";

    private readonly TestServices _t = new();
    private readonly FakeToaster _toaster = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly List<AlertFireDto> _fired = [];
    private readonly AlertNotifier _notifier;
    private bool _silent;

    public AlertNotifierTests()
    {
        _notifier = new AlertNotifier(_toaster, _format, _t.Settings, _t.Time, () => _silent, InlineContext.Instance);
        _notifier.Fired += _fired.Add;
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _t.Dispose();
    }

    private async Task StartAsync()
    {
        _notifier.Start(_t.Api, Key);
        await Wait.UntilAsync(() => _t.Api.AlertStreamOpens.Count == 1);
    }

    private async Task SendAsync(params long[] ids)
    {
        foreach (var id in ids)
        {
            await _t.Api.AlertStream.Writer.WriteAsync(new AlertStreamEvent(id, AlertSamples.Fire(id)), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TheFirstRunRemembersTheNewestFireAndSaysNothing()
    {
        _t.Api.FireRows.AddRange([AlertSamples.Fire(1), AlertSamples.Fire(2)]);

        await StartAsync();

        Assert.Empty(_toaster.Shown);
        Assert.Equal(2, _t.Settings.LoadLastFire(Key));
        Assert.Equal([2L], _t.Api.AlertStreamOpens);
    }

    [Fact]
    public async Task WhenNothingFiredSinceTheLastLookItSaysNothing()
    {
        _t.Settings.SaveLastFire(Key, 2);
        _t.Api.FireRows.AddRange([AlertSamples.Fire(1), AlertSamples.Fire(2)]);

        await StartAsync();

        Assert.Empty(_toaster.Shown);
        Assert.Equal([2L], _t.Api.AlertStreamOpens);
    }

    [Fact]
    public async Task FiresWhileTheTerminalWasClosedAreToldInOneToastAndListenedToFromThere()
    {
        _t.Settings.SaveLastFire(Key, 1);
        _t.Api.FireRows.AddRange([AlertSamples.Fire(1), AlertSamples.Fire(2), AlertSamples.Fire(3)]);

        await StartAsync();

        var toast = Assert.Single(_toaster.Shown);
        Assert.Equal(("Alerts fired while the terminal was closed", "2 alerts since you last looked. See the Alerts page.", null, false), (toast.Title, toast.Body, toast.Ticker, toast.Silent));
        Assert.Equal(3, _t.Settings.LoadLastFire(Key));
        Assert.Equal([3L], _t.Api.AlertStreamOpens);
    }

    [Fact]
    public async Task OneMissedFireIsSaidInTheSingularAndAFullPageAsAHundredOrMore()
    {
        _t.Settings.SaveLastFire(Key, 0);
        _t.Api.FireRows.Add(AlertSamples.Fire(1));
        await StartAsync();
        Assert.Contains("1 alert since", _toaster.Shown[0].Body, StringComparison.Ordinal);
        _notifier.Stop();
        await _notifier.WhenStoppedAsync();

        var other = new FakeApi();
        other.FireRows.AddRange(Enumerable.Range(1, 120).Select(i => AlertSamples.Fire(i)));
        _t.Settings.SaveLastFire("other:5280", 0);
        _notifier.Start(other, "other:5280");
        await Wait.UntilAsync(() => other.AlertStreamOpens.Count == 1);

        Assert.Contains("100 or more alerts since", _toaster.Shown[1].Body, StringComparison.Ordinal);
        Assert.Equal([100L], other.AlertStreamOpens);
    }

    [Fact]
    public async Task AFireBecomesAToastWithTheMarketsNameWhatFiredAndWhenAndOpensTheMarketWhenClicked()
    {
        await StartAsync();

        await SendAsync(7);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 1 && _fired.Count == 1);

        var toast = _toaster.Shown[0];
        Assert.Equal(("Washington vs San Francisco", "price above 0.60 (now 0.61) · 2026-10-20 20:02:11", "KX-A", false), (toast.Title, toast.Body, toast.Ticker, toast.Silent));
        Assert.Equal(7, _fired[0].Id);
        Assert.Equal(7, _t.Settings.LoadLastFire(Key));
    }

    [Fact]
    public async Task TheToastNamesTheTickerWhenTheServerCannotSayWhatItIsAndFollowsTheSoundSetting()
    {
        _t.Api.MarketFails = new ApiUnavailableException("down");
        _silent = true;
        await StartAsync();

        await SendAsync(1);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 1);

        Assert.Equal(("KX-A", true), (_toaster.Shown[0].Title, _toaster.Shown[0].Silent));
    }

    [Fact]
    public async Task AMarketIsOnlyAskedForOnce()
    {
        await StartAsync();
        await SendAsync(1);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 1);
        _t.Api.MarketFails = new ApiUnavailableException("down");

        await SendAsync(2);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 2);

        Assert.Equal("Washington vs San Francisco", _toaster.Shown[1].Title);
    }

    [Fact]
    public async Task OnlyTheFirstThreeFiresOfABurstGetAToastAndTheRestAreOneToastWhenTheWindowEnds()
    {
        await StartAsync();

        await SendAsync(1, 2, 3, 4, 5);
        await Wait.UntilAsync(() => _fired.Count == 5 && _toaster.Shown.Count == 3 && _t.Time.PendingTimers == 1);

        _t.Time.FireNext();
        await Wait.UntilAsync(() => _toaster.Shown.Count == 4);

        var summary = _toaster.Shown[3];
        Assert.Equal(("2 more alerts fired", "See the Alerts page.", null), (summary.Title, summary.Body, summary.Ticker));
    }

    [Fact]
    public async Task OneFireOverTheLimitIsSaidInTheSingularAndANewWindowStartsFresh()
    {
        await StartAsync();
        await SendAsync(1, 2, 3, 4);
        await Wait.UntilAsync(() => _fired.Count == 4 && _toaster.Shown.Count == 3 && _t.Time.PendingTimers == 1);
        _t.Time.FireNext();
        await Wait.UntilAsync(() => _toaster.Shown.Count == 4);
        Assert.Equal("1 more alert fired", _toaster.Shown[3].Title);

        await SendAsync(5);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 5);

        Assert.Equal("KX-A", _toaster.Shown[4].Ticker);
    }

    [Fact]
    public async Task AFireAfterTheWindowHasEndedWithoutATimerStartsANewWindowAndFlushesWhatWasHeld()
    {
        await StartAsync();
        await SendAsync(1, 2, 3, 4);
        await Wait.UntilAsync(() => _fired.Count == 4 && _t.Time.PendingTimers == 1);
        _t.Time.Advance(AlertNotifier.BurstWindow);

        await SendAsync(5);
        await Wait.UntilAsync(() => _toaster.Shown.Count == 5);

        Assert.Equal(["KX-A", "KX-A", "KX-A", null, "KX-A"], _toaster.Shown.Select(t => t.Ticker));
        Assert.Equal("1 more alert fired", _toaster.Shown[3].Title);
    }

    [Fact]
    public async Task StoppingEndsTheListeningLetsGoOfTheClientAndIgnoresLaterFires()
    {
        await StartAsync();

        _notifier.Stop();
        await _notifier.WhenStoppedAsync();

        Assert.True(_t.Api.Disposed);
        Assert.Empty(_toaster.Shown);
    }

    [Fact]
    public async Task StartingAgainStopsTheFirstListening()
    {
        await StartAsync();
        var second = new FakeApi();

        _notifier.Start(second, Key);
        await Wait.UntilAsync(() => second.AlertStreamOpens.Count == 1);

        await Wait.UntilAsync(() => _t.Api.Disposed);
        Assert.Single(second.AlertStreamOpens);
        Assert.Throws<ArgumentNullException>(() => _notifier.Start(null!, Key));
        Assert.Throws<ArgumentNullException>(() => _notifier.Start(second, null!));
    }

    [Fact]
    public async Task ATokenTheServerRefusesEndsTheListeningQuietly()
    {
        _t.Api.AlertStreamFails = Samples.Unauthorized();

        _notifier.Start(_t.Api, Key);
        await _notifier.WhenStoppedAsync();

        Assert.Empty(_toaster.Shown);
        Assert.True(_t.Api.Disposed);
    }

    [Fact]
    public async Task IfTheCatchUpFailsItListensFromWhereItWas()
    {
        _t.Settings.SaveLastFire(Key, 5);
        _t.Api.AlertFails = new ApiUnavailableException("down");

        await StartAsync();

        Assert.Empty(_toaster.Shown);
        Assert.Equal([5L], _t.Api.AlertStreamOpens);
    }
}

public sealed class AlertsViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly AlertsViewModel _model;

    public AlertsViewModelTests()
    {
        _model = new AlertsViewModel(_format, _dialogs);
        _t.Api.AlertRows.AddRange([AlertSamples.Alert(1), AlertSamples.Alert(2, "KX-B", "spread_above", "0.050000", 300, paused: true, lastFired: "2026-10-20T19:00:00.000000+00:00")]);
        _t.Api.FireRows.AddRange([AlertSamples.Fire(1), AlertSamples.Fire(2, "KX-B", "volume_spike", "5", "7.5")]);
    }

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private async Task OpenAsync()
    {
        _model.Attach(_t.Api);
        await _model.LoadAsync();
    }

    [Fact]
    public async Task TheAlertsAndTheLatestFiresAreShownInWords()
    {
        await OpenAsync();

        Assert.Equal(["KX-A", "KX-B"], _model.Alerts.Select(a => a.Ticker));
        Assert.Equal(["price above 0.60", "spread above 0.05"], _model.Alerts.Select(a => a.Condition));
        Assert.Equal(["60 s", "5m"], _model.Alerts.Select(a => a.Cooldown));
        Assert.Equal(["active", "paused"], _model.Alerts.Select(a => a.State));
        Assert.Equal(["Pause", "Resume"], _model.Alerts.Select(a => a.PauseText));
        Assert.Equal(["never", "2026-10-20 19:00:00"], _model.Alerts.Select(a => a.LastFired));
        Assert.Equal([new FireRowViewModel("2026-10-20 20:02:11", "KX-B", "volume spike ×5", "×7.5"), new FireRowViewModel("2026-10-20 20:02:11", "KX-A", "price above 0.60", "0.61")], _model.Fires);
        Assert.False(_model.NoAlerts);
        Assert.False(_model.NoFires);
        Assert.False(_model.HasError);
    }

    [Fact]
    public void WithNothingToShowThePageSaysSo()
    {
        Assert.True(_model.NoAlerts);
        Assert.Equal("Alerts", _model.Title);
        Assert.False(_model.AddCommand.CanExecute(null));
        Assert.False(_model.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailureToReadIsShownOnThePage()
    {
        _t.Api.AlertFails = new ApiUnavailableException("no route");

        await OpenAsync();

        Assert.Equal("Could not read your alerts: no route", _model.Error);
        Assert.True(_model.HasError);
    }

    [Fact]
    public async Task TheFormChecksTheThresholdInTheUnitOfTheKindAndTheCooldownAndEnablesAddOnlyWhenAllIsWell()
    {
        await OpenAsync();
        Assert.Equal("Type the market's ticker.", _model.Problem);
        Assert.False(_model.HasProblem);

        _model.Ticker = "KX-C";
        Assert.Equal("Type a threshold.", _model.Problem);
        Assert.False(_model.HasProblem);
        _model.Threshold = "5";
        Assert.True(_model.HasProblem);
        Assert.Equal(_model.Hint, _model.Problem);
        Assert.False(_model.AddCommand.CanExecute(null));

        _model.Kind = _model.Kinds.Single(k => k.Key == "volume_spike");
        Assert.Null(_model.Problem);
        Assert.Contains("whole factor", _model.Hint, StringComparison.Ordinal);
        Assert.True(_model.AddCommand.CanExecute(null));

        _model.Cooldown = "5";
        Assert.Equal("The cooldown is 10 to 86400 seconds.", _model.Problem);
        Assert.True(_model.HasProblem);
        _model.Cooldown = "abc";
        Assert.NotNull(_model.Problem);
        _model.Cooldown = "30";
        Assert.Null(_model.Problem);
        _model.Kind = null!;   // a box whose choices were just replaced may offer nothing
        Assert.Equal("volume spike", _model.Kind.Text);
    }

    [Fact]
    public async Task AddingMakesTheAlertAndClearsTheThreshold()
    {
        await OpenAsync();
        _model.Ticker = " KX-C ";
        _model.Kind = _model.Kinds.Single(k => k.Key == "oi_change");
        _model.Threshold = "500";
        _model.Cooldown = "120";

        await _model.AddCommand.RunAsync();

        Assert.Contains("create KX-C oi_change 500 120", _t.Api.AlertCalls);
        Assert.Equal(["KX-A", "KX-B", "KX-C"], _model.Alerts.Select(a => a.Ticker));
        Assert.Equal(string.Empty, _model.Threshold);
        Assert.Equal(string.Empty, _model.Error);
    }

    [Fact]
    public async Task AServerRefusalToAddIsShownAndTheFormIsKept()
    {
        await OpenAsync();
        _model.Ticker = "KX-NONE";
        _model.Threshold = "0.6";
        _t.Api.AlertFails = new ApiException(HttpStatusCode.NotFound, "market_not_found");

        await _model.AddCommand.RunAsync();

        Assert.Equal("The server does not know a market called KX-NONE.", _model.Error);
        Assert.Equal("0.6", _model.Threshold);
        Assert.Equal(2, _model.Alerts.Count);
    }

    [Fact]
    public async Task AddingWithAnUnusableFormDoesNothing()
    {
        await OpenAsync();
        _model.Ticker = "KX-C";
        var before = _t.Api.AlertCalls.Count;

        await _model.AddCommand.RunAsync();
        _model.Ticker = string.Empty;

        Assert.Equal(before, _t.Api.AlertCalls.Count);
    }

    [Fact]
    public async Task PauseAndResumeToggleTheAlert()
    {
        await OpenAsync();

        await _model.Alerts[0].ToggleCommand.RunAsync();
        Assert.Equal(["paused", "paused"], _model.Alerts.Select(a => a.State));
        await _model.Alerts[1].ToggleCommand.RunAsync();
        Assert.Equal(["paused", "active"], _model.Alerts.Select(a => a.State));
        Assert.Equal(["pause 1", "resume 2"], _t.Api.AlertCalls.Skip(2).Take(2));
    }

    [Fact]
    public async Task AFailedPauseIsShown()
    {
        await OpenAsync();
        _t.Api.AlertFails = new ApiUnavailableException("down");

        await _model.Alerts[0].ToggleCommand.RunAsync();

        Assert.Equal("Could not pause the alert: down", _model.Error);
    }

    [Fact]
    public async Task DeletingAsksFirstAndOnlyDeletesWhenAgreed()
    {
        await OpenAsync();
        _dialogs.Confirm = false;

        await _model.Alerts[0].DeleteCommand.RunAsync();
        Assert.Equal(2, _model.Alerts.Count);
        Assert.Equal("Delete alert: Delete the alert on KX-A (price above 0.60)?", _dialogs.Asked.Single());

        _dialogs.Confirm = true;
        await _model.Alerts[0].DeleteCommand.RunAsync();

        Assert.Equal(["KX-B"], _model.Alerts.Select(a => a.Ticker));
    }

    [Fact]
    public async Task ANewFireIsAddedToTheListAndMarksItsAlert()
    {
        await OpenAsync();

        _model.OnFire(AlertSamples.Fire(3));

        Assert.Equal("KX-A", _model.Fires[0].Ticker);
        Assert.Equal(3, _model.Fires.Count);
        Assert.Equal("2026-10-20 20:02:11", _model.Alerts[0].LastFired);
    }

    [Fact]
    public async Task TheTimesFollowTheZone()
    {
        await OpenAsync();

        _format.Zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

        Assert.Equal("2026-10-20 22:02:11", _model.Fires[0].Time);
        Assert.Equal("2026-10-20 21:00:00", _model.Alerts[1].LastFired);
    }

    [Fact]
    public async Task StartingANewAlertFillsTheTickerAndClearsTheRest()
    {
        await OpenAsync();
        _model.Threshold = "0.7";
        _model.Cooldown = "99";

        _model.StartNew("KX-Z");

        Assert.Equal(("KX-Z", string.Empty, "60", string.Empty), (_model.Ticker, _model.Threshold, _model.Cooldown, _model.Error));
    }

    [Fact]
    public async Task TheSoundBoxDecidesWhetherTheToastsAreSilent()
    {
        Assert.True(_model.Sound);
        Assert.False(_model.Silent);

        _model.Sound = false;

        Assert.True(_model.Silent);
        await OpenAsync();
    }

    [Fact]
    public async Task RereadingAndDetachingWork()
    {
        await OpenAsync();
        _t.Api.AlertRows.Add(AlertSamples.Alert(3, "KX-C"));

        await _model.RefreshCommand.RunAsync();
        Assert.Equal(3, _model.Alerts.Count);

        _model.Detach();
        Assert.Empty(_model.Alerts);
        Assert.Empty(_model.Fires);
        Assert.False(_model.RefreshCommand.CanExecute(null));
        Assert.Throws<ArgumentNullException>(() => _model.Attach(null!));
    }

    [Fact]
    public void TheAlertsPageShowsWithoutBindingErrors()
    {
        _model.Dispose();
        var (texts, rows, fires) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = new AlertsViewModel(_format, _dialogs);
            model.Attach(_t.Api);
            model.LoadAsync().GetAwaiter().GetResult();
            var view = new AlertsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var found = VisualTreeKit.Texts(view).ToList();
            var grids = VisualTreeKit.All<DataGrid>(view).ToList();
            Assert.Empty(errors.Messages);
            return (found, grids[0].Items.Count, grids[1].Items.Count);
        });

        Assert.Contains("New alert", texts);
        Assert.Contains("Your alerts", texts);
        Assert.Contains("Recent fires (newest first)", texts);
        Assert.Contains("price above 0.60", texts);
        Assert.Equal((2, 2), (rows, fires));
    }
}

public sealed class AlertWiringTests : IDisposable
{
    private static readonly Uri Server = new("https://kaldb:5280");

    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toaster = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly StatusViewModel _status;
    private readonly MarketsViewModel _markets;
    private readonly AlertsViewModel _alerts;
    private readonly AlertNotifier _notifier;
    private readonly MainViewModel _model;
    private int _activations;

    public AlertWiringTests()
    {
        _status = new StatusViewModel(_format, _t.Time);
        _markets = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        _alerts = new AlertsViewModel(_format, _dialogs);
        _notifier = new AlertNotifier(_toaster, _format, _t.Settings, _t.Time, () => _alerts.Silent, InlineContext.Instance);
        _model = new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, _notifier, _toaster);
        _model.ActivationRequested += () => _activations++;
        _t.Settings.SaveServer(Server);
        _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
        _t.Api.AlertRows.Add(AlertSamples.Alert(1));
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _alerts.Dispose();
        _status.Dispose();
        _markets.Dispose();
        _t.Dispose();
    }

    private object? Page => _model.CurrentPage;

    [Fact]
    public async Task ConnectingLoadsTheAlertsAndStartsListeningForFires()
    {
        await _model.StartAsync();

        await Wait.UntilAsync(() => _alerts.Alerts.Count == 1 && _t.Api.AlertStreamOpens.Count == 1);
        Assert.Contains("fires  100", _t.Api.AlertCalls);
    }

    [Fact]
    public async Task AFireThatArrivesShowsOnTheAlertsPageAndAsAToast()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.AlertStreamOpens.Count == 1);

        await _t.Api.AlertStream.Writer.WriteAsync(new AlertStreamEvent(1, AlertSamples.Fire(1)), TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => _alerts.Fires.Count == 1 && _toaster.Shown.Count == 1);

        Assert.Equal("KX-A", _toaster.Shown[0].Ticker);
    }

    [Fact]
    public async Task AskingForAnAlertOnARowShowsTheFormOnTheAlertsPageWithTheMarketFilledIn()
    {
        await _model.StartAsync();
        _markets.Selected = _markets.Rows[0];
        var line = _markets.Menu.Choices.Single(c => c.Text == "Create alert…");

        await line.Command.RunAsync();

        Assert.Same(_alerts, Page);
        Assert.Equal("KX-A", _alerts.Ticker);
    }

    [Fact]
    public async Task AskingForAnAlertOnTheOpenMarketDoesTheSame()
    {
        await _model.StartAsync();
        await _markets.OpenAsync(_markets.Rows[0]);
        Assert.DoesNotContain(_markets.Detail!.Watchlists!.Choices, c => c.Text == "Create alert…");

        _markets.Detail.AlertCommand.Execute(null);

        Assert.Same(_alerts, Page);
        Assert.Equal("KX-A", _alerts.Ticker);
    }

    [Fact]
    public async Task AClickOnAToastOpensTheMarketOnTheMarketsPageAndBringsTheWindowForward()
    {
        await _model.StartAsync();
        _model.Selected = _model.Navigation[2];

        _toaster.Click("KX-A");
        await Wait.UntilAsync(() => _markets.Detail is not null);

        Assert.Equal(("KX-A", 1), (_markets.Detail!.Ticker, _activations));
        Assert.Same(_markets, Page);
    }

    [Fact]
    public async Task AClickThatStartedTheTerminalWaitsForTheConnectionThenOpensTheMarket()
    {
        _toaster.Click("KX-A");
        Assert.Null(_markets.Detail);
        Assert.Same(_markets, Page);

        await _model.StartAsync();
        await Wait.UntilAsync(() => _markets.Detail is not null);

        Assert.Equal("KX-A", _markets.Detail!.Ticker);
    }

    [Fact]
    public void AClickOnAToastWithNoMarketShowsTheAlertsPage()
    {
        _toaster.Click(null);

        Assert.Same(_alerts, Page);
        Assert.Equal(1, _activations);
    }

    [Fact]
    public void TheModelNeedsItsAlertPieces()
    {
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, null!, _notifier, _toaster));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, null!, _toaster));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, _notifier, null!));
    }
}
