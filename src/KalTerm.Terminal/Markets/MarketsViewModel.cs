using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>A way to rank the list: the screener's sort key and what the person reads.</summary>
internal sealed record SortChoice(string Key, string Text);

/// <summary>A status to show; a <see langword="null"/> value is every status.</summary>
internal sealed record StatusChoice(string? Value, string Text);

/// <summary>
/// The Markets page: the screener as a grid, 50 markets at a time, ranked on the server by the chosen figure, filtered by status, minimum volume and a search text, with live prices for the rows on show. Next and
/// Previous move between pages (the server's cursor only goes forward, so the cursors of the pages visited are kept). The markets on show are the ones the feed watches; when the page, the ranking or a filter changes the
/// feed is told, and the stream follows without reconnecting. A market opens into its detail.
/// </summary>
internal sealed partial class MarketsViewModel : ObservableObject, IDisposable
{
    /// <summary>Markets per page; the most one stream can watch is 50, and the page is what is watched.</summary>
    public const int PageSize = 50;

    /// <summary>How long typing must pause before the list is asked again.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the age of the figures is redrawn.</summary>
    public static readonly TimeSpan AgeInterval = TimeSpan.FromSeconds(15);

    /// <summary>Figures older than this are marked as old (the analyzer runs about every minute).</summary>
    public static readonly TimeSpan OldFigures = TimeSpan.FromMinutes(5);

    private const string FeedOwner = "list";

    private readonly Formatting _format;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly List<string?> _cursors = [null];   // the cursor that fetches each page visited; the first page has none
    private readonly Dictionary<string, MarketRowViewModel> _byTicker = new(StringComparer.Ordinal);
    private IKalTermApi? _api;
    private MarketFeed? _feed;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _ageLoop;
    private int _page;
    private string? _nextCursor;
    private string _searchText = string.Empty;
    private StatusChoice _status;
    private string _minVolumeText = string.Empty;
    private SortChoice _sort;
    private bool _descending = true;
    private bool _loading;
    private string _error = string.Empty;
    private bool _stale;
    private string _ageText = string.Empty;
    private bool _figuresOld;
    private string _feedText = "not live";
    private DateTimeOffset? _loadedAt;
    private long? _ageAtLoad;
    private bool _hasRun;
    private MarketRowViewModel? _selected;
    private MarketDetailViewModel? _detail;

    /// <summary>Creates the page.</summary>
    /// <param name="format">How values and times look.</param>
    /// <param name="time">The clock the waits use.</param>
    /// <param name="context">Where the feed raises its events; the UI thread's context at <see cref="Attach"/> when omitted.</param>
    public MarketsViewModel(Formatting format, TimeProvider time, SynchronizationContext? context = null)
    {
        _format = format;
        _time = time;
        _context = context;
        Statuses =
        [
            new StatusChoice(null, "Any status"),
            new StatusChoice("active", "Active"),
            new StatusChoice("closed", "Closed"),
            new StatusChoice("determined", "Determined"),
            new StatusChoice("finalized", "Finalized"),
            new StatusChoice("inactive", "Inactive"),
            new StatusChoice("unlisted", "Unlisted"),
        ];
        Sorts =
        [
            new SortChoice("volume", "24-hour volume"),
            new SortChoice("trades", "24-hour trades"),
            new SortChoice("change", "24-hour change"),
            new SortChoice("last", "Last price"),
            new SortChoice("spread", "Spread"),
            new SortChoice("open_interest", "Open interest"),
            new SortChoice("volatility", "Volatility"),
        ];
        _status = Statuses[1];
        _sort = Sorts[0];
        NextCommand = new AsyncCommand(NextAsync, ex => Error = ex.Message, () => CanNext);
        PreviousCommand = new AsyncCommand(PreviousAsync, ex => Error = ex.Message, () => CanPrevious);
        RefreshCommand = new AsyncCommand(RefreshAsync, ex => Error = ex.Message);
        ToggleOrderCommand = new RelayCommand(() => Descending = !Descending);
        OpenCommand = new AsyncCommand(() => OpenAsync(Selected), ex => Error = ex.Message, () => Selected is not null);
    }

    public string Title { get; } = "Markets";

    public ObservableCollection<MarketRowViewModel> Rows { get; } = [];

    public IReadOnlyList<StatusChoice> Statuses { get; }

    public IReadOnlyList<SortChoice> Sorts { get; }

    public AsyncCommand NextCommand { get; }

    public AsyncCommand PreviousCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand OpenCommand { get; }

    public RelayCommand ToggleOrderCommand { get; }

    /// <summary>What to look for in the ticker or the yes label. Fewer than two characters ask for nothing; typing waits <see cref="SearchDelay"/> before the list is asked.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var before = EffectiveSearch;
            if (Set(ref _searchText, value) && before != EffectiveSearch)
            {
                ScheduleReload();
            }
        }
    }

    public StatusChoice StatusFilter
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                ReloadSoon(immediately: true);
            }
        }
    }

    /// <summary>Only markets with at least this many contracts traded in 24 hours (digits with up to two decimals); anything else is ignored and marked.</summary>
    public string MinVolumeText
    {
        get => _minVolumeText;
        set
        {
            var before = MinVolume;
            if (Set(ref _minVolumeText, value))
            {
                Raise(nameof(MinVolumeInvalid));
                if (before != MinVolume)
                {
                    ScheduleReload();
                }
            }
        }
    }

    public bool MinVolumeInvalid => _minVolumeText.Trim().Length > 0 && MinVolume is null;

    public SortChoice Sort
    {
        get => _sort;
        set
        {
            if (Set(ref _sort, value))
            {
                ReloadSoon(immediately: true);
            }
        }
    }

    /// <summary>Largest first (the default) or smallest first; a market without a value for the ranking is last either way.</summary>
    public bool Descending
    {
        get => _descending;
        set
        {
            if (Set(ref _descending, value))
            {
                Raise(nameof(OrderText));
                ReloadSoon(immediately: true);
            }
        }
    }

    public string OrderText => _descending ? "Largest first" : "Smallest first";

    public string PageText => $"Page {_page + 1}";

    public bool CanPrevious => _page > 0 && !_loading;

    public bool CanNext => _nextCursor is not null && !_loading;

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (Set(ref _loading, value))
            {
                RaiseNavigation();
            }
        }
    }

    /// <summary>True once a page has been asked for, so the view can tell "nothing yet" from "nothing found".</summary>
    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    /// <summary>The page is empty and the last ask worked.</summary>
    public bool NothingFound => _hasRun && Rows.Count == 0 && _error.Length == 0 && !_loading;

    /// <summary>Why the last ask failed; empty when it worked.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(NothingFound));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>The rows on show are from before the last failed ask, so they are shown grey.</summary>
    public bool Stale
    {
        get => _stale;
        private set => Set(ref _stale, value);
    }

    /// <summary>How old the screener's figures are (the 24-hour change and volume).</summary>
    public string AgeText
    {
        get => _ageText;
        private set => Set(ref _ageText, value);
    }

    /// <summary>The figures are older than <see cref="OldFigures"/>: the analyzer may have stalled.</summary>
    public bool FiguresOld
    {
        get => _figuresOld;
        private set => Set(ref _figuresOld, value);
    }

    /// <summary>Whether live prices are arriving.</summary>
    public string FeedText
    {
        get => _feedText;
        private set => Set(ref _feedText, value);
    }

    public MarketRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The market opened over the list, or <see langword="null"/>.</summary>
    public MarketDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            if (Set(ref _detail, value))
            {
                Raise(nameof(ShowingDetail));
                Raise(nameof(ShowingList));
            }
        }
    }

    public bool ShowingDetail => _detail is not null;

    /// <summary>The list can be used: no market is open over it.</summary>
    public bool ShowingList => _detail is null;

    private string? EffectiveSearch => _searchText.Trim().Length >= 2 ? _searchText.Trim() : null;

    private string? MinVolume => MinVolumeShape().IsMatch(_minVolumeText.Trim()) ? _minVolumeText.Trim() : null;

    /// <summary>Points the page at a server: opens the live feed and clears the old list. The caller then loads the first page.</summary>
    /// <param name="create">Makes a client. Called twice: one for the page's own requests, one for the feed.</param>
    public void Attach(Func<IKalTermApi> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        Detach();
        _api = create();
        _feed = new MarketFeed(create(), _time, _context);
        _feed.Tick += OnTick;
        _feed.StateChanged += OnFeedState;
        _feed.Reset += OnReset;
        _feed.Start();
        _ageLoop = new CancellationTokenSource();
        _ = AgeLoopAsync(_ageLoop.Token);
    }

    /// <summary>Stops everything and lets go of the server.</summary>
    public void Detach()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _ageLoop?.Cancel();
        _ageLoop?.Dispose();
        _ageLoop = null;
        CloseDetail();
        if (_feed is not null)
        {
            _feed.Tick -= OnTick;
            _feed.StateChanged -= OnFeedState;
            _feed.Reset -= OnReset;
            _feed.Dispose();
            _feed = null;
        }

        (_api as IDisposable)?.Dispose();
        _api = null;
        Rows.Clear();
        _byTicker.Clear();
        _cursors.Clear();
        _cursors.Add(null);
        _page = 0;
        _nextCursor = null;
        _loadedAt = null;
        HasRun = false;
        Error = string.Empty;
        Stale = false;
        AgeText = string.Empty;
        FeedText = "not live";
        RaiseNavigation();
    }

    public void Dispose() => Detach();

    /// <summary>Loads the first page again (used when the ranking, a filter or the search changes, and to start).</summary>
    /// <returns>A task that completes when the page is shown or has failed.</returns>
    public async Task LoadAsync()
    {
        await FetchAsync(0, null).ConfigureAwait(true);
    }

    /// <summary>Loads the page being shown again, with fresh figures.</summary>
    /// <returns>A task that completes when the page is shown or has failed.</returns>
    public Task RefreshAsync() => FetchAsync(_page, _cursors[_page]);

    private Task NextAsync() => _nextCursor is { } next ? FetchAsync(_page + 1, next) : Task.CompletedTask;

    private Task PreviousAsync() => _page > 0 ? FetchAsync(_page - 1, _cursors[_page - 1]) : Task.CompletedTask;

    /// <summary>Opens a market over the list.</summary>
    /// <param name="row">The market.</param>
    /// <returns>A task that completes when its detail has loaded (or failed to).</returns>
    public async Task OpenAsync(MarketRowViewModel? row)
    {
        if (row is null || _api is null || _feed is null)
        {
            return;
        }

        CloseDetail();
        var detail = new MarketDetailViewModel(row.Ticker, _api, _feed, _format, _time);
        detail.Closed += CloseDetail;
        Detail = detail;
        await detail.LoadAsync().ConfigureAwait(true);
    }

    private void CloseDetail()
    {
        if (_detail is null)
        {
            return;
        }

        var detail = _detail;
        detail.Closed -= CloseDetail;
        Detail = null;
        detail.Dispose();
    }

    private async Task FetchAsync(int page, string? cursor)
    {
        if (_api is null)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var source = new CancellationTokenSource();
        _load = source;
        var token = source.Token;
        Loading = true;
        try
        {
            var shown = await _api.GetScreenerAsync(new ScreenQuery(Sort.Key, _descending ? "desc" : "asc", StatusFilter.Value, MinVolume, PageSize, cursor, null, EffectiveSearch), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Show(shown, page, cursor);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            if (!token.IsCancellationRequested)
            {
                Error = ex is ApiException { Code: "unauthorized" } ? "the server did not accept the token" : ex.Message;
                Stale = Rows.Count > 0;
                HasRun = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_load, source))
            {
                Loading = false;
            }
        }
    }

    private void Show(ScreenerPage shown, int page, string? cursor)
    {
        _page = page;
        if (_cursors.Count <= page)
        {
            _cursors.Add(cursor);
        }
        else
        {
            _cursors[page] = cursor;
        }

        _nextCursor = shown.NextCursor;
        Rows.Clear();
        _byTicker.Clear();
        foreach (var row in shown.Rows)
        {
            var model = new MarketRowViewModel(row);
            Rows.Add(model);
            _byTicker[row.Ticker] = model;
        }

        _loadedAt = _time.GetUtcNow();
        _ageAtLoad = shown.RefreshedAt is null ? null : shown.AgeSeconds ?? 0;
        Error = string.Empty;
        Stale = false;
        HasRun = true;
        Raise(nameof(NothingFound));
        Raise(nameof(PageText));
        RaiseNavigation();
        UpdateAge();
        _feed?.SetWanted(FeedOwner, [.. Rows.Select(r => r.Ticker)]);
    }

    private void ReloadSoon(bool immediately)
    {
        if (_api is null)
        {
            return;
        }

        if (immediately)
        {
            _debounce?.Cancel();
            _ = SafeAsync(LoadAsync);
        }
    }

    private void ScheduleReload()
    {
        if (_api is null)
        {
            return;
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        var source = new CancellationTokenSource();
        _debounce = source;
        _ = DebounceAsync(source.Token);
    }

    private async Task DebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDelay, _time, token).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // typing went on: a later ask replaces this one
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task SafeAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void OnTick(MarketTickDto tick)
    {
        if (_byTicker.TryGetValue(tick.Ticker, out var row))
        {
            row.Apply(tick.Tick);
        }
    }

    private void OnFeedState(FeedState state, string note)
    {
        var word = state switch
        {
            FeedState.Live => "live",
            FeedState.Connecting => "connecting…",
            FeedState.Reconnecting => "reconnecting…",
            _ => "not live",
        };
        FeedText = note.Length > 0 ? $"{word} ({note})" : word;
    }

    private void OnReset() => _ = SafeAsync(RefreshAsync);

    private async Task AgeLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(AgeInterval, _time, token).ConfigureAwait(true);
                UpdateAge();
            }
        }
        catch (OperationCanceledException)
        {
            // detached
        }
    }

    private void UpdateAge()
    {
        if (_loadedAt is not { } at)
        {
            AgeText = string.Empty;
            FiguresOld = false;
            return;
        }

        if (_ageAtLoad is not { } seconds)
        {
            AgeText = "the metrics analyzer has not finished a run yet";
            FiguresOld = false;
            return;
        }

        var age = TimeSpan.FromSeconds(seconds) + (_time.GetUtcNow() - at);
        AgeText = $"24-hour figures {Formatting.Age((long)age.TotalSeconds)}";
        FiguresOld = age > OldFigures;
    }

    private void RaiseNavigation()
    {
        Raise(nameof(CanPrevious));
        Raise(nameof(CanNext));
        NextCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
    }

    [GeneratedRegex(@"^[0-9]{1,12}(\.[0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MinVolumeShape();
}
