using System.Collections.ObjectModel;
using KalTerm.Client;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Status;

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
internal sealed class MainViewModel : ObservableObject
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
    private IKalTermApi? _api;

    public MainViewModel(ClientServices services, TerminalSettingsStore settings, IDialogs dialogs, Formatting format, StatusViewModel status, MarketsViewModel markets)
    {
        _services = services;
        _settings = settings;
        _dialogs = dialogs;
        _format = format;
        Status = status;
        Markets = markets;
        Navigation = [new NavigationItem(status.Title, status), new NavigationItem(markets.Title, markets)];
        _selected = Navigation[0];
        Zones = new ObservableCollection<ZoneChoice>(BuildZones(services.SystemZone));
        _zone = Zones[0];
        StartCommand = new AsyncCommand(StartAsync, ex => StateText = ex.Message);
        ConnectCommand = new AsyncCommand(() => ConnectDialogAsync(null), ex => StateText = ex.Message);
        RetryCommand = new AsyncCommand(RetryAsync, ex => StateText = ex.Message);
        Status.Succeeded += OnStatusSucceeded;
        Status.Failed += OnStatusFailed;
    }

    public StatusViewModel Status { get; }

    public MarketsViewModel Markets { get; }

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
            _ = OpenMarketsAsync(_server);
        }
    }

    private void OnStatusFailed(Exception ex)
    {
        switch (ex)
        {
            case TlsTrustException trust:
                State = ConnectionState.NeedsAttention;
                StateText = trust.Failure == TrustFailure.Changed ? "the server's certificate changed" : "the server's certificate is not trusted";
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
            var connect = new ConnectViewModel(_services, _server);
            switch (reason)
            {
                case TlsTrustException { Failure: TrustFailure.Changed } changed:
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

    /// <summary>Points the Markets page at the server and loads its first page. A failure is shown on the page itself.</summary>
    private async Task OpenMarketsAsync(Uri server)
    {
        try
        {
            Markets.Attach(() => _services.CreateApi(server));
            await Markets.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            StateText = "could not open the markets: " + ex.Message;
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
            var added = new ZoneChoice(stored, TimeZoneInfo.FindSystemTimeZoneById(stored).DisplayName);
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

    private static List<ZoneChoice> BuildZones(TimeZoneInfo system)
    {
        var zones = new List<ZoneChoice> { new(null, $"This computer ({system.DisplayName})"), new("UTC", "UTC") };
        zones.AddRange(TimeZoneInfo.GetSystemTimeZones()
            .Where(z => z.Id != "UTC")
            .OrderBy(z => z.BaseUtcOffset)
            .ThenBy(z => z.DisplayName, StringComparer.Ordinal)
            .Select(z => new ZoneChoice(z.Id, z.DisplayName)));
        return zones;
    }
}
