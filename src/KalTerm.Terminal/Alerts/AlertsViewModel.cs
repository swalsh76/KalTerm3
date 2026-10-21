using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Alerts;

/// <summary>One alert as the grid shows it, with the commands that act on it.</summary>
internal sealed class AlertRowViewModel
{
    public AlertRowViewModel(AlertDto alert, Formatting format, AsyncCommand toggle, AsyncCommand delete)
    {
        Id = alert.Id;
        Ticker = alert.Ticker;
        Condition = AlertText.Condition(alert.Kind, alert.Threshold);
        Cooldown = alert.CooldownSeconds < 120 ? $"{alert.CooldownSeconds} s" : Formatting.Age(alert.CooldownSeconds).Replace(" ago", string.Empty, StringComparison.Ordinal);
        State = alert.Paused ? "paused" : "active";
        PauseText = alert.Paused ? "Resume" : "Pause";
        LastFired = alert.LastFiredAt is null ? "never" : format.Moment(alert.LastFiredAt);
        ToggleCommand = toggle;
        DeleteCommand = delete;
    }

    public int Id { get; }

    public string Ticker { get; }

    public string Condition { get; }

    public string Cooldown { get; }

    public string State { get; }

    public string PauseText { get; }

    public string LastFired { get; }

    public AsyncCommand ToggleCommand { get; }

    public AsyncCommand DeleteCommand { get; }
}

/// <summary>One fire as the list shows it.</summary>
/// <param name="Time">When it fired, in the chosen zone.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Condition">What the alert watches for.</param>
/// <param name="Observed">What was seen.</param>
internal sealed record FireRowViewModel(string Time, string Ticker, string Condition, string Observed);

/// <summary>
/// The Alerts page: the person's alerts (pause, resume, delete), a form that makes one, and the latest fires, newest first, with new ones arriving as they happen. The form checks the threshold as it is typed, in the unit of the
/// kind chosen, and says what a usable one looks like; the server judges again. The "Sound" box decides whether the toasts make a sound.
/// </summary>
internal sealed partial class AlertsViewModel : ObservableObject, IDisposable
{
    private readonly AlertService _service = new();
    private readonly Formatting _format;
    private readonly IDialogs _dialogs;
    private AlertKindChoice _kind = AlertKindChoice.All[0];
    private string _ticker = string.Empty;
    private string _threshold = string.Empty;
    private string _cooldown = "60";
    private string _error = string.Empty;
    private bool _sound = true;
    private IKalTermApi? _api;

    /// <summary>Creates the page.</summary>
    /// <param name="format">Writes the times in the chosen zone.</param>
    /// <param name="dialogs">Asks before an alert is deleted.</param>
    public AlertsViewModel(Formatting format, IDialogs dialogs)
    {
        _format = format;
        _dialogs = dialogs;
        AddCommand = new AsyncCommand(AddAsync, ex => Error = ex.Message, () => Problem is null && _api is not null);
        RefreshCommand = new AsyncCommand(LoadAsync, ex => Error = ex.Message, () => _api is not null);
        _service.Changed += Rebuild;
        _format.ZoneChanged += Rebuild;
    }

    public string Title { get; } = "Alerts";

    public ObservableCollection<AlertRowViewModel> Alerts { get; } = [];

    public ObservableCollection<FireRowViewModel> Fires { get; } = [];

    public IReadOnlyList<AlertKindChoice> Kinds { get; } = AlertKindChoice.All;

    public AsyncCommand AddCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    /// <summary>The market the new alert is for.</summary>
    public string Ticker
    {
        get => _ticker;
        set
        {
            if (Set(ref _ticker, value))
            {
                FormChanged();
            }
        }
    }

    public AlertKindChoice Kind
    {
        get => _kind;
        set
        {
            if ((object?)value is not null && Set(ref _kind, value))
            {
                Raise(nameof(Hint));
                FormChanged();
            }
        }
    }

    /// <summary>The threshold in the unit of the kind.</summary>
    public string Threshold
    {
        get => _threshold;
        set
        {
            if (Set(ref _threshold, value))
            {
                FormChanged();
            }
        }
    }

    /// <summary>The least time between two fires, in seconds.</summary>
    public string Cooldown
    {
        get => _cooldown;
        set
        {
            if (Set(ref _cooldown, value))
            {
                FormChanged();
            }
        }
    }

    /// <summary>What a usable threshold is for the kind chosen.</summary>
    public string Hint => _kind.Hint;

    /// <summary>What is wrong with the form so far, or <see langword="null"/> when the alert can be made. Nothing is said about a field that is still empty.</summary>
    public string? Problem =>
        _ticker.Trim().Length == 0 ? "Type the market's ticker."
        : _kind.Problem(_threshold) is { } threshold ? threshold
        : CooldownSeconds() is null ? $"The cooldown is {AlertService.MinCooldown} to {AlertService.MaxCooldown} seconds."
        : null;

    /// <summary>Whether to show <see cref="Problem"/>: the person has typed something in the threshold or the cooldown and it will not do.</summary>
    public bool HasProblem => Problem is not null && (_threshold.Length > 0 || _cooldown != "60");

    /// <summary>Why the last request failed; empty when it worked.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>Whether the toasts make a sound.</summary>
    public bool Sound
    {
        get => _sound;
        set => Set(ref _sound, value);
    }

    /// <summary>The fires that came in are told to the person: a toast, and a line in <see cref="Fires"/>.</summary>
    public bool Silent => !_sound;

    /// <summary>Points the page at a server. The caller then loads it.</summary>
    /// <param name="api">The client for the page's requests.</param>
    public void Attach(IKalTermApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        Detach();
        _api = api;
        _service.Attach(api);
        RaiseCommands();
    }

    /// <summary>Lets go of the server and empties the page.</summary>
    public void Detach()
    {
        (_api as IDisposable)?.Dispose();
        _api = null;
        _service.Attach(null);
        Error = string.Empty;
        RaiseCommands();
    }

    public void Dispose()
    {
        Detach();
        _service.Changed -= Rebuild;
        _format.ZoneChanged -= Rebuild;
    }

    /// <summary>Reads the alerts and the latest fires.</summary>
    /// <returns>A task that completes when they are shown or have failed.</returns>
    public async Task LoadAsync()
    {
        Error = await _service.LoadAsync().ConfigureAwait(true) ?? string.Empty;
    }

    /// <summary>A fire the stream pushed.</summary>
    /// <param name="fire">The fire.</param>
    public void OnFire(AlertFireDto fire) => _service.AddFire(fire);

    /// <summary>Starts a new alert for a market: the form is cleared and its ticker filled in.</summary>
    /// <param name="ticker">The market.</param>
    public void StartNew(string ticker)
    {
        Ticker = ticker;
        Threshold = string.Empty;
        Cooldown = "60";
        Error = string.Empty;
    }

    private int? CooldownSeconds() =>
        CooldownShape().IsMatch(_cooldown.Trim()) && int.Parse(_cooldown.Trim(), CultureInfo.InvariantCulture) is >= AlertService.MinCooldown and <= AlertService.MaxCooldown ? int.Parse(_cooldown.Trim(), CultureInfo.InvariantCulture) : null;

    private void FormChanged()
    {
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        AddCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        AddCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private async Task AddAsync()
    {
        if (CooldownSeconds() is not { } cooldown || Problem is not null)
        {
            return;
        }

        var problem = await _service.CreateAsync(new CreateAlertRequest(_ticker.Trim(), _kind.Key, _threshold.Trim(), cooldown)).ConfigureAwait(true);
        Error = problem ?? string.Empty;
        if (problem is null)
        {
            Threshold = string.Empty;
        }
    }

    private async Task ToggleAsync(int id, bool pause) => Error = await _service.SetPausedAsync(id, pause).ConfigureAwait(true) ?? string.Empty;

    private async Task DeleteAsync(int id, string ticker, string condition)
    {
        if (!await _dialogs.ConfirmAsync("Delete alert", $"Delete the alert on {ticker} ({condition})?").ConfigureAwait(true))
        {
            return;
        }

        Error = await _service.DeleteAsync(id).ConfigureAwait(true) ?? string.Empty;
    }

    private void Rebuild()
    {
        Alerts.Clear();
        foreach (var alert in _service.Alerts)
        {
            var id = alert.Id;
            var condition = AlertText.Condition(alert.Kind, alert.Threshold);
            Alerts.Add(new AlertRowViewModel(
                alert,
                _format,
                new AsyncCommand(() => ToggleAsync(id, !alert.Paused), ex => Error = ex.Message),
                new AsyncCommand(() => DeleteAsync(id, alert.Ticker, condition), ex => Error = ex.Message)));
        }

        Fires.Clear();
        foreach (var fire in _service.Fires)
        {
            Fires.Add(new FireRowViewModel(_format.Moment(fire.FiredAt), fire.Ticker, AlertText.Condition(fire.Kind, fire.Threshold), AlertText.Amount(fire.Kind, fire.Value)));
        }

        Raise(nameof(NoAlerts));
        Raise(nameof(NoFires));
    }

    /// <summary>The page has no alerts to show.</summary>
    public bool NoAlerts => Alerts.Count == 0;

    /// <summary>The page has no fires to show.</summary>
    public bool NoFires => Fires.Count == 0;

    [GeneratedRegex(@"^[0-9]{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CooldownShape();
}
