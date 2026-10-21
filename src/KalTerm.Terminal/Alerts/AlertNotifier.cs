using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Alerts;

/// <summary>
/// Listens for the person's alert fires for as long as the terminal is connected and turns each into a Windows toast that opens the market when clicked. The first three fires in a window of
/// <see cref="BurstWindow"/> each get a toast; the rest of that window become one toast ("4 more alerts fired") when it ends. When the terminal starts it says how many alerts fired since the last fire it saw, in one toast,
/// then listens from there, so nothing is told twice. The newest fire seen is remembered per server in the terminal's settings.
/// </summary>
internal sealed class AlertNotifier : IDisposable
{
    /// <summary>Toasts each given to a fire in a burst, before the rest are gathered into one.</summary>
    public const int ToastsPerBurst = 3;

    /// <summary>How long a burst lasts, from its first fire.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);

    private readonly IToaster _toaster;
    private readonly Formatting _format;
    private readonly TerminalSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly Func<bool> _silent;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private Task _running = Task.CompletedTask;
    private DateTimeOffset? _windowStart;
    private int _shown;
    private int _held;
    private ITimer? _flush;
    private bool _stopped = true;

    /// <summary>Creates the notifier.</summary>
    /// <param name="toaster">Shows the toasts.</param>
    /// <param name="format">Writes the times in the chosen zone.</param>
    /// <param name="settings">Remembers the newest fire seen.</param>
    /// <param name="time">The clock the burst window uses.</param>
    /// <param name="silent">Whether toasts should make no sound now.</param>
    /// <param name="context">Where fires are handed to the page; the UI thread's context when omitted.</param>
    public AlertNotifier(IToaster toaster, Formatting format, TerminalSettingsStore settings, TimeProvider time, Func<bool> silent, SynchronizationContext? context = null)
    {
        _toaster = toaster;
        _format = format;
        _settings = settings;
        _time = time;
        _silent = silent;
        _context = context ?? SynchronizationContext.Current;
    }

    /// <summary>A fire arrived, in order; raised on the UI thread (through the context given).</summary>
    public event Action<AlertFireDto>? Fired;

    /// <summary>Starts listening. A notifier already listening is stopped first.</summary>
    /// <param name="api">A client for the stream and the lookups; the notifier lets go of it when it stops.</param>
    /// <param name="serverKey">The server, for remembering the newest fire seen.</param>
    public void Start(IKalTermApi api, string serverKey)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(serverKey);
        Stop();
        _stopped = false;
        _run = new CancellationTokenSource();
        _running = RunAsync(api, serverKey, _run.Token);
    }

    /// <summary>Stops listening and lets go of the client.</summary>
    public void Stop()
    {
        _stopped = true;
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        lock (_gate)
        {
            _flush?.Dispose();
            _flush = null;
            _windowStart = null;
            _held = 0;
            _shown = 0;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Waits until the listening has ended (after <see cref="Stop"/>); for tests.</summary>
    /// <returns>A task that completes when the listening has ended.</returns>
    public Task WhenStoppedAsync() => _running;

    private async Task RunAsync(IKalTermApi api, string serverKey, CancellationToken token)
    {
        try
        {
            var last = await CatchUpAsync(api, serverKey, token).ConfigureAwait(false);
            var watcher = new AlertWatcher((from, ct) => api.StreamAlertFiresAsync(from, ct), _time);
            await watcher.RunAsync(last, fire => OnFireAsync(api, serverKey, fire, token), _ => { }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            // a token the server does not accept ends the listening; the pages say so when they ask for something themselves
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }

    /// <summary>Counts the fires since the newest one seen, tells the person in one toast, and returns where to listen from.</summary>
    private async Task<long?> CatchUpAsync(IKalTermApi api, string serverKey, CancellationToken token)
    {
        var seen = _settings.LoadLastFire(serverKey);
        try
        {
            var missed = await api.GetAlertFiresAsync(seen, AlertService.MaxFires, token).ConfigureAwait(false);
            if (seen is not null && missed.Fires.Count > 0)
            {
                var count = missed.Fires.Count >= AlertService.MaxFires ? $"{AlertService.MaxFires} or more" : missed.Fires.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Show(new ToastMessage("Alerts fired while the terminal was closed", $"{count} {(missed.Fires.Count == 1 ? "alert" : "alerts")} since you last looked. See the Alerts page.", null, _silent()));
            }

            if (missed.LastId is { } newest)
            {
                _settings.SaveLastFire(serverKey, newest);
                return newest;
            }
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            // listen from where we were; the stream replays what it can
        }

        return seen;
    }

    private async Task OnFireAsync(IKalTermApi api, string serverKey, AlertFireDto fire, CancellationToken token)
    {
        _settings.SaveLastFire(serverKey, fire.Id);
        var toast = TakeToast();
        if (_context is { } context)
        {
            context.Post(_ => Fired?.Invoke(fire), null);
        }
        else
        {
            Fired?.Invoke(fire);
        }

        if (!toast)
        {
            return;
        }

        var name = await NameOfAsync(api, fire.Ticker, token).ConfigureAwait(false);
        var body = $"{AlertText.Condition(fire.Kind, fire.Threshold)} (now {AlertText.Amount(fire.Kind, fire.Value)}) · {_format.Moment(fire.FiredAt)}";
        Show(new ToastMessage(name, body, fire.Ticker, _silent()));
    }

    /// <summary>Whether this fire gets a toast of its own; if not, it is counted for the gathered one at the end of the window.</summary>
    private bool TakeToast()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            if (_windowStart is not { } start || now - start >= BurstWindow)
            {
                FlushHeld();
                _windowStart = now;
                _shown = 0;
            }

            if (_shown < ToastsPerBurst)
            {
                _shown++;
                return true;
            }

            _held++;
            _flush ??= _time.CreateTimer(_ => { lock (_gate) { FlushHeld(); } }, null, _windowStart.Value + BurstWindow - now, Timeout.InfiniteTimeSpan);
            return false;
        }
    }

    private void FlushHeld()
    {
        _flush?.Dispose();
        _flush = null;
        if (_held > 0)
        {
            var held = _held;
            _held = 0;
            Show(new ToastMessage($"{held} more {(held == 1 ? "alert" : "alerts")} fired", "See the Alerts page.", null, _silent()));
        }
    }

    private void Show(ToastMessage message) => _toaster.Show(message);

    /// <summary>What the market is called, asked once for each ticker; the ticker itself when the server cannot say.</summary>
    private async Task<string> NameOfAsync(IKalTermApi api, string ticker, CancellationToken token)
    {
        lock (_gate)
        {
            if (_names.TryGetValue(ticker, out var known))
            {
                return known;
            }
        }

        var name = ticker;
        try
        {
            var market = await api.GetMarketAsync(ticker, token).ConfigureAwait(false);
            name = MarketNames.Name(market.EventTitle, market.Title, market.YesLabel, ticker);
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            // the ticker will do
        }

        lock (_gate)
        {
            _names[ticker] = name;
        }

        return name;
    }
}
