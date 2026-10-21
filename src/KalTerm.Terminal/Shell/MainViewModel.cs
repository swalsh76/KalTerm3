using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using KalTerm.Client;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Layouts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Trading;

namespace KalTerm.Terminal.Shell;

/// <summary>Whether the terminal is talking to a server.</summary>
internal enum ConnectionState
{
    /// <summary>No server chosen yet.</summary>
    NotConnected,

    /// <summary>Asking the server.</summary>
    Connecting,

    /// <summary>The server answered.</summary>
    Connected,

    /// <summary>The server did not answer; the page keeps its last content, greyed.</summary>
    Unreachable,

    /// <summary>The server needs a trusted certificate or a token first.</summary>
    NeedsAttention,
}

/// <summary>A time zone the person can choose. A <see langword="null"/> <see cref="Id"/> means "this computer's zone".</summary>
internal sealed record ZoneChoice(string? Id, string Text);

/// <summary>A page in the navigation list.</summary>
internal sealed record NavigationItem(string Title, object Page);

/// <summary>
/// The main window: which server, whether it answers, the page being shown and the time zone. It connects on start with the server used last, opens the connect dialog when there is none or when the server needs a
/// trusted certificate or a token, and shows every time in the zone the person chose (stored on the server, like <c>kterm settings tz</c>).
/// </summary>
internal sealed partial class MainViewModel : ObservableObject, ILayoutTarget
{
    private readonly ClientServices _services;
    private readonly TerminalSettingsStore _settings;
    private readonly IDialogs _dialogs;
    private readonly Formatting _format;
    private ConnectionState _state = ConnectionState.NotConnected;
    private string _stateText = "not connected";
    private Uri? _server;
    private NavigationItem? _selected;
    private ZoneChoice _zone;
    private bool _asking;
    private bool _applyingZone;
    /// <summary>How long closing the window waits for the layout to be saved.</summary>
    public static readonly TimeSpan LayoutFlushLimit = TimeSpan.FromSeconds(2);

    private readonly AlertNotifier _notifier;
    private string? _pendingTicker;
    private bool _marketsReady;
    private IKalTermApi? _api;

    public MainViewModel(ClientServices services, TerminalSettingsStore settings, IDialogs dialogs, Formatting format, StatusViewModel status, MarketsViewModel markets, AlertsViewModel alerts, AlertNotifier notifier, IToaster toaster, TradingViewModel trading)
    {
        ArgumentNullException.ThrowIfNull(trading);
        Trading = trading;
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(toaster);
        Alerts = alerts;
        _notifier = notifier;
        Layouts = new LayoutCoordinator(this, dialogs, settings, services.Time);
        _services = services;
        _settings = settings;
        _dialogs = dialogs;
        _format = format;
        Status = status;
        Markets = markets;
        Navigation = [new NavigationItem(status.Title, status), new NavigationItem(markets.Title, markets), new NavigationItem(alerts.Title, alerts), new NavigationItem(trading.Title, trading)];
        _selected = Navigation[0];
        Zones = new ObservableCollection<ZoneChoice>(BuildZones(services.SystemZone, services.Time.GetUtcNow()));
        _zone = Zones[0];
        StartCommand = new AsyncCommand(StartAsync, ex => StateText = ex.Message);
        ConnectCommand = new AsyncCommand(() => ConnectDialogAsync(null), ex => StateText = ex.Message);
        RetryCommand = new AsyncCommand(RetryAsync, ex => StateText = ex.Message);
        Status.Succeeded += OnStatusSucceeded;
        Status.Failed += OnStatusFailed;
        Markets.AlertRequested += OnAlertRequested;
        Markets.TradeRequested += OnTradeRequested;
        Markets.LayoutChanged += Layouts.Changed;
        Alerts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlertsViewModel.Sound))
            {
                Layouts.Changed();
            }
        };
        _notifier.Fired += Alerts.OnFire;
        toaster.Activated += OnToastActivated;
    }

    /// <summary>The window should come to the front (a toast was clicked).</summary>
    public event Action? ActivationRequested;

    public StatusViewModel Status { get; }

    public MarketsViewModel Markets { get; }

    public AlertsViewModel Alerts { get; }

    /// <summary>The trading page: the account on Kalshi, the order ticket and the kill switch.</summary>
    public TradingViewModel Trading { get; }

    /// <summary>The saved layouts: which one is in use, saving, switching, deleting.</summary>
    public LayoutCoordinator Layouts { get; }

    /// <summary>The main window as the layout sees it; set by the window.</summary>
    public IWindowPlacement? Placement { get; set; }

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public ObservableCollection<ZoneChoice> Zones { get; }

    public AsyncCommand StartCommand { get; }

    public AsyncCommand ConnectCommand { get; }

    public AsyncCommand RetryCommand { get; }

    public NavigationItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(CurrentPage));
                Layouts.Changed();
            }
        }
    }

    public object? CurrentPage => _selected?.Page;

    public ConnectionState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string ServerText => _server is null ? "no server" : EndpointKey.Of(_server);

    /// <summary>The zone choice shown; changing it stores the choice on the server and redraws every time.</summary>
    public ZoneChoice Zone
    {
        get => _zone;
        set
        {
            if (Set(ref _zone, value) && !_applyingZone)
            {
                _format.Zone = ZoneOf(value);
                _ = StoreZoneAsync(value);
            }
        }
    }

    /// <summary>Connects to the server used last, or asks for one.</summary>
    public async Task StartAsync()
    {
        _ = Trading.ConnectAsync();   // Kalshi is reached directly, not through the KalTerm server, and a failure here never stops anything else
        if (_settings.LoadServer() is { } server)
        {
            await ConnectAsync(server).ConfigureAwait(true);
        }
        else
        {
            await ConnectDialogAsync(null).ConfigureAwait(true);
        }
    }

    /// <summary>Connects to a server: asks it once, then keeps asking.</summary>
    public async Task ConnectAsync(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        Raise(nameof(ServerText));
        State = ConnectionState.Connecting;
        StateText = "connecting";
        _api = _services.CreateApi(server);
        Status.Attach(_api);
        if (await Status.RefreshAsync().ConfigureAwait(true))
        {
            Status.StartPolling();
        }
    }

    private async Task RetryAsync()
    {
        if (_server is not null)
        {
            await ConnectAsync(_server).ConfigureAwait(true);
        }
        else
        {
            await ConnectDialogAsync(null).ConfigureAwait(true);
        }
    }

    private void OnStatusSucceeded()
    {
        var first = State != ConnectionState.Connected;
        State = ConnectionState.Connected;
        StateText = "connected";
        if (first && _server is not null)
        {
            _settings.SaveServer(_server);
            _ = LoadZoneAsync();
            _ = OpenPagesAsync(_server);
        }
    }

    private void OnStatusFailed(Exception ex)
    {
        switch (ex)
        {
            case TlsTrustException trust:
                State = ConnectionState.NeedsAttention;
                StateText = trust.Failure switch
                {
                    TrustFailure.Changed => "the server's certificate changed",
                    TrustFailure.Expired => "the server's certificate has expired",
                    _ => "the server's certificate is not trusted",
                };
                _ = ConnectDialogAsync(trust);
                break;
            case ApiException { Code: "unauthorized" }:
                State = ConnectionState.NeedsAttention;
                StateText = "log in";
                _ = ConnectDialogAsync(ex);
                break;
            default:
                State = ConnectionState.Unreachable;
                StateText = "not reachable";
                break;
        }
    }

    /// <summary>Opens the connect dialog. A server that needs attention opens it at the step that fixes it.</summary>
    private async Task ConnectDialogAsync(Exception? reason)
    {
        if (_asking)
        {
            return;
        }

        _asking = true;
        try
        {
            Status.Detach();
            Markets.Detach();
            _marketsReady = false;
            Layouts.Detach();
            _notifier.Stop();
            Alerts.Detach();
            var connect = new ConnectViewModel(_services, _server);
            switch (reason)
            {
                case TlsTrustException { Failure: TrustFailure.Changed or TrustFailure.Expired } changed:
                    await connect.StartAtTrustAsync(_server!, changed.Message).ConfigureAwait(true);
                    break;
                case TlsTrustException:
                    await connect.StartAtTrustAsync(_server!, string.Empty).ConfigureAwait(true);
                    break;
                case ApiException when _server is not null:
                    connect.StartAtLogin(_server, "The server did not accept the stored token. Paste a new one.");
                    break;
            }

            if (await _dialogs.ShowConnectAsync(connect).ConfigureAwait(true) is { } chosen)
            {
                await ConnectAsync(chosen).ConfigureAwait(true);
            }
            else
            {
                State = ConnectionState.NotConnected;
                StateText = "not connected";
            }
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>
    /// Reads the layout in use and lays the terminal out with it, then opens the pages: they load once, already laid out. The layout is a convenience, so a server that cannot give it leaves the normal layout and the pages open anyway.
    /// </summary>
    private async Task OpenPagesAsync(Uri server)
    {
        if (_api is not null)
        {
            await Layouts.StartAsync(_api, EndpointKey.Of(server)).ConfigureAwait(true);
        }

        await Task.WhenAll(OpenMarketsAsync(server), OpenAlertsAsync(server)).ConfigureAwait(true);
    }

    /// <summary>The window, a page or a filter changed: the layout is saved a moment after the last change.</summary>
    public void NotifyLayoutChanged() => Layouts.Changed();

    /// <summary>Saves the layout now if it changed. The window calls this as it closes; the wait is bounded, and a server that does not answer never keeps the window open.</summary>
    /// <returns>A task that completes when it is saved or the time is up.</returns>
    public async Task FlushLayoutAsync()
    {
        using var limit = new CancellationTokenSource(LayoutFlushLimit, _services.Time);
        try
        {
            await Layouts.FlushAsync(limit.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // the server did not answer in time: the layout of a moment ago stays
        }
    }

    /// <inheritdoc/>
    LayoutState ILayoutTarget.Capture() => new(
        LayoutState.Current,
        Placement?.Capture(),
        Selected?.Title,
        Markets.CaptureLayout(),
        Markets.Preferences.Capture(),
        Alerts.Sound);

    /// <inheritdoc/>
    void ILayoutTarget.Apply(LayoutState state)
    {
        if (state.Window is { } window)
        {
            Placement?.Apply(window);
        }

        if (_pendingTicker is null && state.Page is { } page && Navigation.FirstOrDefault(n => n.Title == page) is { } navigation)
        {
            Selected = navigation;   // a toast that started the terminal has already chosen the page
        }

        Markets.ApplyLayout(state.Markets);
        Markets.Preferences.Apply(state.Chart);
        if (state.Sound is { } sound)
        {
            Alerts.Sound = sound;
        }
    }

    /// <summary>Points the Markets page at the server and loads its first page. A failure is shown on the page itself.</summary>
    private async Task OpenMarketsAsync(Uri server)
    {
        try
        {
            Markets.Attach(() => _services.CreateApi(server));
            await Markets.LoadAsync().ConfigureAwait(true);
            _marketsReady = true;
            if (_pendingTicker is { } ticker)
            {
                _pendingTicker = null;
                await Markets.OpenTickerAsync(ticker).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            StateText = "could not open the markets: " + ex.Message;
        }
    }

    /// <summary>Points the Alerts page at the server, loads it and starts listening for fires. A failure is shown on the page itself.</summary>
    private async Task OpenAlertsAsync(Uri server)
    {
        Alerts.Attach(_services.CreateApi(server));
        _notifier.Start(_services.CreateApi(server), EndpointKey.Of(server));
        await Alerts.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>A trade was asked for on a market: show the Trading page with an order ticket for it.</summary>
    private void OnTradeRequested(string ticker, string? price)
    {
        Selected = Navigation.Single(n => ReferenceEquals(n.Page, Trading));
        _ = Trading.OpenTicketAsync(ticker, price);
    }

    /// <summary>An alert was asked for on a market: show the form with the market in it.</summary>
    private void OnAlertRequested(string ticker)
    {
        Alerts.StartNew(ticker);
        Selected = Navigation.Single(n => ReferenceEquals(n.Page, Alerts));
    }

    /// <summary>A toast was clicked: bring the window forward and, when it named a market, open it on the Markets page (once the page has a server, if the click started the terminal).</summary>
    private void OnToastActivated(string? ticker)
    {
        ActivationRequested?.Invoke();
        if (ticker is null)
        {
            Selected = Navigation.Single(n => ReferenceEquals(n.Page, Alerts));
            return;
        }

        Selected = Navigation.Single(n => ReferenceEquals(n.Page, Markets));
        if (_marketsReady)
        {
            _ = Markets.OpenTickerAsync(ticker);
        }
        else
        {
            _pendingTicker = ticker;
        }
    }

    private async Task LoadZoneAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var stored = await _api.GetTimeZoneSettingAsync().ConfigureAwait(true);
            var choice = FindOrAddZone(stored.Value);
            _applyingZone = true;
            try
            {
                Zone = choice;
                _format.Zone = ZoneOf(choice);
            }
            finally
            {
                _applyingZone = false;
            }
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            // the zone follows this computer until the server can be asked again
        }
    }

    /// <summary>The choice for a stored zone name. A name <c>kterm</c> stored (an IANA name such as America/Denver) is not in the list of this computer's zones by id, so it is added when this computer knows it.</summary>
    private ZoneChoice FindOrAddZone(string? stored)
    {
        if (stored is null)
        {
            return Zones[0];
        }

        if (Zones.FirstOrDefault(z => string.Equals(z.Id, stored, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return known;
        }

        try
        {
            var added = new ZoneChoice(stored, ZoneText(TimeZoneInfo.FindSystemTimeZoneById(stored), _services.Time.GetUtcNow()));
            Zones.Add(added);
            return added;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Zones[0];
        }
    }

    private async Task StoreZoneAsync(ZoneChoice choice)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.SetTimeZoneSettingAsync(choice.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            StateText = "could not store the time zone: " + ex.Message;
        }
    }

    private TimeZoneInfo ZoneOf(ZoneChoice choice) =>
        choice.Id is null ? _services.SystemZone : TimeZoneInfo.FindSystemTimeZoneById(choice.Id);

    /// <summary>
    /// A zone's name with the offset in effect at the moment, for example <c>Mountain Time (US &amp; Canada), now UTC-06:00</c>. Windows names a zone with its standard-time offset ("(UTC-07:00) Mountain Time"), which is
    /// not the offset the times shown are in while daylight saving applies.
    /// </summary>
    /// <param name="zone">The zone.</param>
    /// <param name="now">The moment the offset is taken at.</param>
    /// <returns>The text for the zone box.</returns>
    internal static string ZoneText(TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var name = StandardOffsetPrefix().Replace(zone.DisplayName, string.Empty);
        var offset = zone.GetUtcOffset(now);
        return $"{name}, now UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm}";
    }

    [GeneratedRegex(@"^\(UTC(?:[+-]\d{2}:\d{2})?\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex StandardOffsetPrefix();

    private static List<ZoneChoice> BuildZones(TimeZoneInfo system, DateTimeOffset now)
    {
        var zones = new List<ZoneChoice> { new(null, $"This computer ({ZoneText(system, now)})"), new("UTC", "UTC") };
        zones.AddRange(TimeZoneInfo.GetSystemTimeZones()
            .Where(z => z.Id != "UTC")
            .OrderBy(z => z.BaseUtcOffset)
            .ThenBy(z => z.DisplayName, StringComparer.Ordinal)
            .Select(z => new ZoneChoice(z.Id, ZoneText(z, now))));
        return zones;
    }
}
