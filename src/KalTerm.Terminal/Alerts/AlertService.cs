using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Alerts;

/// <summary>
/// The person's alerts and the fires that came of them, kept in memory: read together, changed by what the server accepted, and fed the fires the stream pushes. Every method that asks the server answers with a
/// sentence when it failed and <see langword="null"/> when it worked, like <see cref="Markets.WatchlistService"/>.
/// </summary>
internal sealed class AlertService
{
    /// <summary>The most alerts the server allows one person.</summary>
    public const int MaxAlerts = 200;

    /// <summary>The shortest cooldown the server allows, in seconds.</summary>
    public const int MinCooldown = 10;

    /// <summary>The longest cooldown the server allows, in seconds (a day).</summary>
    public const int MaxCooldown = 86_400;

    /// <summary>The most fires kept for show; older ones drop off.</summary>
    public const int MaxFires = 100;

    private readonly List<AlertDto> _alerts = [];
    private readonly List<AlertFireDto> _fires = [];
    private IKalTermApi? _api;

    /// <summary>The alerts or the fires changed.</summary>
    public event Action? Changed;

    /// <summary>The alerts, oldest first.</summary>
    public IReadOnlyList<AlertDto> Alerts => _alerts;

    /// <summary>The fires, newest first.</summary>
    public IReadOnlyList<AlertFireDto> Fires => _fires;

    /// <summary>Whether the alerts have been read since the last <see cref="Attach"/>.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Points the service at a server (or at none) and forgets what it held.</summary>
    /// <param name="api">The client for the person's own requests, or <see langword="null"/> to detach.</param>
    public void Attach(IKalTermApi? api)
    {
        _api = api;
        Loaded = false;
        if (_alerts.Count > 0 || _fires.Count > 0)
        {
            _alerts.Clear();
            _fires.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>Reads the alerts and the latest fires.</summary>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> LoadAsync()
    {
        if (_api is not { } api)
        {
            return null;
        }

        try
        {
            var alerts = await api.GetAlertsAsync().ConfigureAwait(true);
            var fires = await api.GetAlertFiresAsync(null, MaxFires).ConfigureAwait(true);
            _alerts.Clear();
            _alerts.AddRange(alerts.Alerts);
            _fires.Clear();
            _fires.AddRange(fires.Fires.OrderByDescending(f => f.Id).Take(MaxFires));
            Loaded = true;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, "read your alerts");
        }
    }

    /// <summary>Makes an alert.</summary>
    /// <param name="request">The ticker, kind, threshold and cooldown.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> CreateAsync(CreateAlertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_api is not { } api)
        {
            return "Not connected.";
        }

        try
        {
            var made = await api.CreateAlertAsync(request).ConfigureAwait(true);
            _alerts.Add(made);
            Changed?.Invoke();
            return null;
        }
        catch (ApiException ex) when (ex.Code == "market_not_found")
        {
            return $"The server does not know a market called {request.Ticker}.";
        }
        catch (ApiException ex) when (ex.Code == "limit_reached")
        {
            return $"You already have {MaxAlerts} alerts. Delete one first.";
        }
        catch (ApiException ex) when (ex.Code == "invalid_parameter")
        {
            return "The server did not accept the alert" + (ex.Fields.Count > 0 ? $" ({string.Join(", ", ex.Fields)})." : ".");
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, "make the alert");
        }
    }

    /// <summary>Pauses an alert or resumes it.</summary>
    /// <param name="id">The alert.</param>
    /// <param name="paused"><see langword="true"/> to pause, <see langword="false"/> to resume.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public Task<string?> SetPausedAsync(int id, bool paused) => ChangeAsync(
        id,
        api => api.SetAlertPausedAsync(id, paused),
        () =>
        {
            if (_alerts.FindIndex(a => a.Id == id) is >= 0 and var at)
            {
                _alerts[at] = _alerts[at] with { Paused = paused };
            }
        },
        paused ? "pause the alert" : "resume the alert");

    /// <summary>Deletes an alert.</summary>
    /// <param name="id">The alert.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public Task<string?> DeleteAsync(int id) => ChangeAsync(id, api => api.DeleteAlertAsync(id), () => _alerts.RemoveAll(a => a.Id == id), "delete the alert");

    /// <summary>Takes in a fire the stream pushed: it goes first in the list, and its alert shows when it last fired.</summary>
    /// <param name="fire">The fire.</param>
    public void AddFire(AlertFireDto fire)
    {
        ArgumentNullException.ThrowIfNull(fire);
        if (_fires.Exists(f => f.Id == fire.Id))
        {
            return;
        }

        _fires.Insert(0, fire);
        if (_fires.Count > MaxFires)
        {
            _fires.RemoveRange(MaxFires, _fires.Count - MaxFires);
        }

        if (_alerts.FindIndex(a => a.Id == fire.AlertId) is >= 0 and var at)
        {
            _alerts[at] = _alerts[at] with { LastFiredAt = fire.FiredAt };
        }

        Changed?.Invoke();
    }

    private async Task<string?> ChangeAsync(int id, Func<IKalTermApi, Task> send, Action apply, string doing)
    {
        if (_api is not { } api)
        {
            return "Not connected.";
        }

        try
        {
            await send(api).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "not_found")
        {
            _ = _alerts.RemoveAll(a => a.Id == id);
            Changed?.Invoke();
            return "That alert no longer exists.";
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, doing);
        }

        apply();
        Changed?.Invoke();
        return null;
    }
}
