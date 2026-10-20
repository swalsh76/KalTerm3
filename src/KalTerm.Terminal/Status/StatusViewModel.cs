using KalTerm.Client;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Status;

/// <summary>
/// The status page: what <c>kterm status</c> prints, kept fresh. It asks the server every <see cref="Interval"/>; when a refresh fails it keeps the last page, greys it and says why, so a stale page can never pass for
/// a live one. Failures that need the person (a token refused, a certificate not trusted or changed) are reported through <see cref="Failed"/>; the shell decides what to do about them.
/// </summary>
internal sealed class StatusViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the page asks the server.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Formatting _format;
    private readonly TimeProvider _time;
    private IKalTermApi? _api;
    private Contracts.StatusDto? _last;
    private DateTimeOffset? _updatedAt;
    private StatusPage? _page;
    private string _error = string.Empty;
    private bool _stale;
    private string _updatedText = "not loaded yet";
    private CancellationTokenSource? _polling;

    public StatusViewModel(Formatting format, TimeProvider time)
    {
        _format = format;
        _time = time;
        _format.ZoneChanged += Redraw;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), ex => Error = ex.Message);
    }

    /// <summary>A refresh failed; the shell may need to ask the person for a token or a certificate.</summary>
    public event Action<Exception>? Failed;

    /// <summary>A refresh worked.</summary>
    public event Action? Succeeded;

    public string Title { get; } = "Status";

    public AsyncCommand RefreshCommand { get; }

    public StatusPage? Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(HasPage));
                Raise(nameof(Loading));
                Raise(nameof(HasProblems));
                Raise(nameof(AllClear));
            }
        }
    }

    public bool HasPage => _page is not null;

    /// <summary>Nothing to show yet and no reason why.</summary>
    public bool Loading => _page is null && _error.Length == 0;

    public bool HasProblems => _page is { Problems.Count: > 0 };

    public bool AllClear => _page is { Problems.Count: 0 };

    /// <summary>Why the last refresh failed; empty when it worked.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(Loading));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>The page is older than three intervals, so it is shown grey.</summary>
    public bool Stale
    {
        get => _stale;
        private set => Set(ref _stale, value);
    }

    /// <summary>When the page was last refreshed, for the footer.</summary>
    public string UpdatedText
    {
        get => _updatedText;
        private set => Set(ref _updatedText, value);
    }

    /// <summary>Points the page at a server and forgets the old page.</summary>
    public void Attach(IKalTermApi api)
    {
        Detach();
        _api = api;
        _last = null;
        _updatedAt = null;
        Page = null;
        Error = string.Empty;
        UpdatedText = "not loaded yet";
        Stale = false;
    }

    public void Dispose()
    {
        _format.ZoneChanged -= Redraw;
        Detach();
    }

    /// <summary>Stops polling and lets go of the server.</summary>
    public void Detach()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
        (_api as IDisposable)?.Dispose();
        _api = null;
    }

    /// <summary>Asks the server once.</summary>
    /// <returns><see langword="true"/> when it answered.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return false;
        }

        try
        {
            _last = await _api.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            Error = ex switch
            {
                ApiUnavailableException => ex.Message,
                TlsTrustException => ex.Message,
                ApiException { Code: "unauthorized" } => "the server did not accept the token",
                _ => ex.Message,
            };
            UpdateAge();
            Failed?.Invoke(ex);
            return false;
        }

        _updatedAt = _time.GetUtcNow();
        Error = string.Empty;
        Redraw();
        Succeeded?.Invoke();
        return true;
    }

    /// <summary>Refreshes now and then every <see cref="Interval"/> until <see cref="Detach"/>.</summary>
    public void StartPolling()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = new CancellationTokenSource();
        var token = _polling.Token;
        _ = PollAsync(token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(true);
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // detached
        }
    }

    private void Redraw()
    {
        if (_last is not null)
        {
            Page = StatusPresenter.Present(_last, _format);
        }

        UpdateAge();
    }

    private void UpdateAge()
    {
        if (_updatedAt is not { } at)
        {
            return;
        }

        var age = _time.GetUtcNow() - at;
        Stale = age > Interval * 3;
        UpdatedText = $"updated {_format.Moment(at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture))} ({Formatting.Age((long)age.TotalSeconds)})";
    }
}
