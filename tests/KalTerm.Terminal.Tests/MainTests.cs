using KalTerm.Client;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;

namespace KalTerm.Terminal.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private static readonly Uri Server = new("https://kaldb:5280");

    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly StatusViewModel _status;
    private readonly MarketsViewModel _markets;
    private readonly AlertsViewModel _alerts;
    private readonly AlertNotifier _notifier;
    private readonly FakeToaster _toaster = new();
    private readonly MainViewModel _model;

    public MainViewModelTests()
    {
        _status = new StatusViewModel(_format, _t.Time);
        _markets = new MarketsViewModel(_format, _t.Time);
        _alerts = new AlertsViewModel(_format, _dialogs);
        _notifier = new AlertNotifier(_toaster, _format, _t.Settings, _t.Time, () => _alerts.Silent, InlineContext.Instance);
        _model = new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, _notifier, _toaster);
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _alerts.Dispose();
        _status.Dispose();
        _markets.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithAServerUsedBeforeItConnectsAndShowsTheStatus()
    {
        _t.Settings.SaveServer(Server);

        await _model.StartAsync();

        Assert.Equal(ConnectionState.Connected, _model.State);
        Assert.Equal("connected", _model.StateText);
        Assert.Equal("kaldb:5280", _model.ServerText);
        Assert.True(_status.HasPage);
        Assert.Equal([Server, Server, Server, Server, Server], _t.Created);   // the status page, the Markets page's two (its own requests and its live feed), the Alerts page and the alert listener
        Assert.Empty(_dialogs.Shown);
    }

    [Fact]
    public async Task WithNoServerItAsksAndConnectsToTheAnswer()
    {
        _dialogs.Answer = _ => Server;

        await _model.StartAsync();

        Assert.Single(_dialogs.Shown);
        Assert.Equal(ConnectionState.Connected, _model.State);
        Assert.Equal(Server, _t.Settings.LoadServer());
    }

    [Fact]
    public async Task GivingUpOnTheDialogLeavesItNotConnected()
    {
        await _model.StartAsync();

        Assert.Equal(ConnectionState.NotConnected, _model.State);
        Assert.Equal("not connected", _model.StateText);
        Assert.Equal("no server", _model.ServerText);
        Assert.Null(_t.Settings.LoadServer());
    }

    [Fact]
    public async Task AnUnreachableServerIsShownAndRetryTriesAgain()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StatusFails = new ApiUnavailableException("could not reach kaldb:5280");

        await _model.StartAsync();
        Assert.Equal((ConnectionState.Unreachable, "not reachable"), (_model.State, _model.StateText));

        _t.Api.StatusFails = null;
        await _model.RetryCommand.RunAsync();

        Assert.Equal(ConnectionState.Connected, _model.State);
        Assert.Equal(1 + 5, _t.Created.Count);   // the failed try, then the connection that worked (status, the two of the Markets page, the Alerts page and the alert listener)
    }

    [Fact]
    public async Task RetryWithNoServerAsksForOne()
    {
        _dialogs.Answer = _ => Server;

        await _model.RetryCommand.RunAsync();

        Assert.Equal(ConnectionState.Connected, _model.State);
    }

    [Fact]
    public async Task ATokenTheServerRefusesOpensTheDialogAtLogin()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StatusFails = Samples.Unauthorized();

        await _model.StartAsync();

        var connect = Assert.Single(_dialogs.Shown);
        Assert.True(connect.OnLogin);
        Assert.Contains("did not accept the stored token", connect.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.NotConnected, _model.State);   // the person gave up
    }

    [Fact]
    public async Task ACertificateNotTrustedOpensTheDialogAtTrustAndConnectsAfter()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StatusFails = new TlsTrustException(TrustFailure.NotTrusted, "kaldb:5280", null, Samples.Fingerprint, new InvalidOperationException());
        _dialogs.Answer = connect =>
        {
            _t.Api.StatusFails = null;
            return Server;
        };

        await _model.StartAsync();

        var connect = Assert.Single(_dialogs.Shown);
        Assert.True(connect.OnTrust);
        Assert.False(connect.HasWarning);
        Assert.Equal([("kaldb", 5280)], _t.Probed);
        Assert.Equal(ConnectionState.Connected, _model.State);
    }

    [Fact]
    public async Task AChangedCertificateOpensTheDialogWithAWarning()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StatusFails = new TlsTrustException(TrustFailure.Changed, "kaldb:5280", new string('1', 64), Samples.Fingerprint, new InvalidOperationException());

        await _model.StartAsync();

        var connect = Assert.Single(_dialogs.Shown);
        Assert.True(connect.OnTrust);
        Assert.StartsWith("WARNING: the certificate of kaldb:5280 CHANGED", connect.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyOneDialogIsOpenAtATime()
    {
        _dialogs.Pending = new TaskCompletionSource<Uri?>();

        var first = _model.ConnectCommand.RunAsync();
        var second = _model.ConnectCommand.RunAsync();
        _dialogs.Pending.SetResult(null);
        await first;
        await second;

        Assert.Single(_dialogs.Shown);
    }

    [Fact]
    public async Task TheConnectButtonStartsFromTheCurrentServer()
    {
        _t.Settings.SaveServer(Server);
        await _model.StartAsync();

        await _model.ConnectCommand.RunAsync();

        Assert.Equal("https://kaldb:5280", Assert.Single(_dialogs.Shown).Address);
    }

    [Fact]
    public async Task AFailureToStartIsShownNotThrown()
    {
        _t.Settings.SaveServer(Server);
        _t.CreateApiFails = new InvalidOperationException("the token file cannot be read");

        await _model.StartCommand.RunAsync();

        Assert.Equal("the token file cannot be read", _model.StateText);
    }

    [Fact]
    public async Task TheStoredZoneIsAppliedWhenConnected()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StoredZone = "UTC";

        await _model.StartAsync();

        Assert.Equal("UTC", _model.Zone.Id);
        Assert.Equal(TimeZoneInfo.Utc, _format.Zone);
        Assert.Empty(_t.Api.ZonesStored);   // loading it is not storing it again
    }

    [Fact]
    public async Task AZoneNamedTheWayTheCliStoresItIsRecognised()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StoredZone = "America/Denver";

        await _model.StartAsync();

        Assert.Equal("America/Denver", _model.Zone.Id);
        Assert.Contains(_model.Zone, _model.Zones);
        Assert.Equal(TimeSpan.FromHours(-6), _format.Zone.GetUtcOffset(new DateTime(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    public async Task NoStoredZoneOrAnUnknownOneFollowsThisComputer(string? stored)
    {
        _t.Settings.SaveServer(Server);
        _t.Api.StoredZone = stored;
        _t.SystemZone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        await _model.StartAsync();

        Assert.Null(_model.Zone.Id);
        Assert.StartsWith("This computer (", _model.Zone.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingAZoneRedrawsAndStoresItOnTheServer()
    {
        _t.Settings.SaveServer(Server);
        await _model.StartAsync();
        var denver = _model.Zones.First(z => z.Id == "Mountain Standard Time");

        _model.Zone = denver;
        await Task.Yield();

        Assert.Equal(["Mountain Standard Time"], _t.Api.ZonesStored);
        Assert.Equal("Server status 2026-10-18 17:00:00 -06:00", _status.Page!.Heading);

        _model.Zone = _model.Zones[0];
        Assert.Equal(["Mountain Standard Time", null], _t.Api.ZonesStored);
    }

    [Fact]
    public async Task AZoneThatCannotBeStoredIsReportedButStillUsed()
    {
        _t.Settings.SaveServer(Server);
        await _model.StartAsync();
        _t.Api.ZoneFails = new ApiUnavailableException("lost the server");

        _model.Zone = _model.Zones.First(z => z.Id == "UTC");
        await Task.Yield();

        Assert.Equal("could not store the time zone: lost the server", _model.StateText);
        Assert.Equal(TimeZoneInfo.Utc, _format.Zone);
    }

    [Fact]
    public async Task IfTheZoneCannotBeReadItFollowsThisComputer()
    {
        _t.Settings.SaveServer(Server);
        _t.Api.ZoneFails = new ApiUnavailableException("lost the server");

        await _model.StartAsync();

        Assert.Equal(ConnectionState.Connected, _model.State);
        Assert.Null(_model.Zone.Id);
    }

    [Fact]
    public void TheZoneListStartsWithThisComputerAndUtcThenEveryZoneByOffset()
    {
        Assert.Null(_model.Zones[0].Id);
        Assert.Equal("UTC", _model.Zones[1].Id);
        Assert.True(_model.Zones.Count > 50);
        var offsets = _model.Zones.Skip(2).Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id!).BaseUtcOffset).ToList();
        Assert.Equal(offsets.Order(), offsets);
    }

    [Fact]
    public void TheStatusMarketsAndAlertsPagesAreThePagesForNow()
    {
        Assert.Equal(["Status", "Markets", "Alerts"], _model.Navigation.Select(n => n.Title));
        Assert.Same(_status, _model.CurrentPage);

        _model.Selected = null;
        Assert.Null(_model.CurrentPage);
    }
}
