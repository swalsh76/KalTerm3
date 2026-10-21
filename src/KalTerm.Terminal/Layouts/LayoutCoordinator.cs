using System.Collections.ObjectModel;
using KalTerm.Client;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Layouts;

/// <summary>What the coordinator lays out: it reads the terminal's state to save it and puts a saved state back.</summary>
internal interface ILayoutTarget
{
    /// <summary>The terminal as it is now.</summary>
    /// <returns>The layout to save.</returns>
    LayoutState Capture();

    /// <summary>Lays the terminal out as a saved state says. Parts the state does not hold are left as they are.</summary>
    /// <param name="state">The state.</param>
    void Apply(LayoutState state);
}

/// <summary>
/// The layouts in the top bar: which one is in use, saving it as the terminal changes, switching, saving under a new name and deleting. It reads the layout last used on this computer (remembered per server in the terminal's
/// settings, so two computers with different screens can each keep their own) when the terminal connects, and saves about <see cref="SaveDelay"/> after the last change and once more when the window closes. It never saves
/// before it has read the layout it is about to overwrite, nor over a layout it could not read, so a server that cannot be reached or a layout from a newer terminal is never replaced by the defaults.
/// </summary>
internal sealed class LayoutCoordinator : ObservableObject, IDisposable
{
    /// <summary>The name used when nothing else has been chosen.</summary>
    public const string DefaultName = "default";

    /// <summary>How long after the last change the layout is saved.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly ILayoutTarget _target;
    private readonly IDialogs _dialogs;
    private readonly TerminalSettingsStore _settings;
    private readonly TimeProvider _time;
    private LayoutService? _service;
    private string _serverKey = string.Empty;
    private string _current = DefaultName;
    private string _problem = string.Empty;
    private bool _applying;
    private bool _writable;
    private bool _dirty;
    private bool _busy;
    private CancellationTokenSource? _pending;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="target">What is laid out.</param>
    /// <param name="dialogs">Asks for a name and for confirmation.</param>
    /// <param name="settings">Remembers the layout in use on this computer.</param>
    /// <param name="time">The clock the save delay uses.</param>
    public LayoutCoordinator(ILayoutTarget target, IDialogs dialogs, TerminalSettingsStore settings, TimeProvider time)
    {
        _target = target;
        _dialogs = dialogs;
        _settings = settings;
        _time = time;
        SaveAsCommand = new AsyncCommand(SaveAsAsync, ex => Problem = ex.Message, () => _service is not null);
        DeleteCommand = new AsyncCommand(DeleteAsync, ex => Problem = ex.Message, () => _service is not null && Names.Contains(_current));
    }

    /// <summary>The names of the saved layouts, and the one in use when it has not been saved yet.</summary>
    public ObservableCollection<string> Names { get; } = [];

    public AsyncCommand SaveAsCommand { get; }

    public AsyncCommand DeleteCommand { get; }

    /// <summary>The layout in use. Choosing another saves this one, reads that one and lays the terminal out with it.</summary>
    public string Current
    {
        get => _current;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _current)
            {
                return;
            }

            _ = SwitchAsync(value);
        }
    }

    /// <summary>Why the last request failed or why nothing is being saved; empty when all is well.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
            {
                Raise(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => _problem.Length > 0;

    /// <summary>Whether changes are being saved.</summary>
    public bool Saving => _writable;

    /// <summary>Points the coordinator at a server and reads the layouts and the one to use. The caller waits for this before the pages load, so they load once, laid out.</summary>
    /// <param name="api">The client for the requests.</param>
    /// <param name="serverKey">The server, for remembering the layout in use.</param>
    /// <returns>A task that completes when the layout is applied (or the defaults are kept).</returns>
    public async Task StartAsync(IKalTermApi api, string serverKey)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(serverKey);
        Detach();
        _service = new LayoutService(api);
        _serverKey = serverKey;
        var wanted = _settings.LoadLayoutName(serverKey) is { Length: > 0 } remembered ? remembered : DefaultName;
        var (names, listProblem) = await _service.ListAsync(CancellationToken.None).ConfigureAwait(true);
        SetNames(names);
        if (listProblem is not null)
        {
            Problem = listProblem.TrimEnd('.', ' ') + ". Changes are not saved until the layouts can be read.";
            SetCurrent(wanted);
            return;
        }

        await LoadAsync(wanted, CancellationToken.None).ConfigureAwait(true);
        SaveAsCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Lets go of the server; nothing more is saved.</summary>
    public void Detach()
    {
        CancelPending();
        _service = null;
        _writable = false;
        _dirty = false;
        Problem = string.Empty;
        Names.Clear();
        SaveAsCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        Raise(nameof(Saving));
    }

    public void Dispose() => Detach();

    /// <summary>Something laid out changed: it is saved after <see cref="SaveDelay"/> without another change.</summary>
    public void Changed()
    {
        if (_applying || !_writable || _service is null)
        {
            return;
        }

        _dirty = true;
        CancelPending();
        var source = new CancellationTokenSource();
        _pending = source;
        _ = SaveLaterAsync(source.Token);
    }

    /// <summary>Saves now what has not been saved. The window calls this as it closes; the caller bounds the wait.</summary>
    /// <param name="cancellationToken">Gives up the save.</param>
    /// <returns>A task that completes when it is saved (or there was nothing to save).</returns>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        CancelPending();
        if (_dirty && _writable)
        {
            await SaveNowAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDelay, _time, token).ConfigureAwait(true);
            await SaveNowAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // another change came, or the coordinator was stopped
        }
    }

    private async Task SaveNowAsync(CancellationToken token)
    {
        if (_service is not { } service)
        {
            return;
        }

        _dirty = false;
        var problem = await service.SaveAsync(_current, _target.Capture(), token).ConfigureAwait(true);
        if (problem is not null)
        {
            _dirty = true;
        }

        Problem = problem ?? string.Empty;
        if (problem is null && !Names.Contains(_current))
        {
            SetNames([.. Names, _current]);
        }
    }

    private async Task LoadAsync(string name, CancellationToken token)
    {
        if (_service is not { } service)
        {
            return;
        }

        var read = await service.ReadAsync(name, token).ConfigureAwait(true);
        SetCurrent(name);
        _settings.SaveLayoutName(_serverKey, name);
        if (read.State is { } state)
        {
            Apply(state);
            _writable = true;
            Problem = string.Empty;
        }
        else if (read.Missing)
        {
            _writable = true;   // nothing there to lose: the first change makes it
            Problem = string.Empty;
        }
        else
        {
            _writable = false;
            Problem = read.Problem ?? string.Empty;
        }

        Raise(nameof(Saving));
    }

    private void Apply(LayoutState state)
    {
        _applying = true;
        try
        {
            _target.Apply(state);
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task SwitchAsync(string name)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(name, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SaveAsAsync()
    {
        if (_service is not { } service)
        {
            return;
        }

        var name = await _dialogs.AskNameAsync("Save layout as", "A name for this layout (lower-case letters, digits, dots, dashes and underscores).", s => WatchlistService.NameProblem(s)).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        CancelPending();
        var problem = await service.SaveAsync(name, _target.Capture(), CancellationToken.None).ConfigureAwait(true);
        if (problem is not null)
        {
            Problem = problem;
            return;
        }

        if (!Names.Contains(name))
        {
            SetNames([.. Names, name]);
        }

        SetCurrent(name);
        _settings.SaveLayoutName(_serverKey, name);
        _writable = true;
        _dirty = false;
        Problem = string.Empty;
        Raise(nameof(Saving));
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteAsync()
    {
        if (_service is not { } service || !Names.Contains(_current))
        {
            return;
        }

        var name = _current;
        if (!await _dialogs.ConfirmAsync("Delete layout", $"Delete the layout {name}? The terminal keeps looking the way it does now.").ConfigureAwait(true))
        {
            return;
        }

        CancelPending();
        var problem = await service.DeleteAsync(name, CancellationToken.None).ConfigureAwait(true);
        if (problem is not null)
        {
            Problem = problem;
            return;
        }

        SetNames([.. Names.Where(n => n != name)]);
        _dirty = false;
        Problem = string.Empty;
        if (name == DefaultName)
        {
            SetCurrent(DefaultName);   // what is on screen is the working layout; the next change saves it again
        }
        else
        {
            await LoadAsync(DefaultName, CancellationToken.None).ConfigureAwait(true);
        }

        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void CancelPending()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    private void SetNames(IReadOnlyList<string> names)
    {
        var sorted = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var keep = _current;
        Names.Clear();
        foreach (var name in sorted)
        {
            Names.Add(name);
        }

        // the box lost its selection with the list; the name in use is still the one in use
        _current = keep;
        Raise(nameof(Current));
    }

    private void SetCurrent(string name)
    {
        _current = name;
        if (!Names.Contains(name))
        {
            SetNames([.. Names, name]);
        }

        Raise(nameof(Current));
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
