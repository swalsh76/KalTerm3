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

/// <summary>A choice in the list box: every market (no name) or one of the person's watchlists.</summary>
internal sealed class ListChoice : ObservableObject
{
    private string _text;

    public ListChoice(string? name, string text)
    {
        Name = name;
        _text = text;
    }

    /// <summary>The watchlist's name; <see langword="null"/> for every market.</summary>
    public string? Name { get; }

    /// <summary>What the person reads: the name and how many markets it holds.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }
}

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
    private readonly IDialogs? _dialogs;
    private readonly WatchlistService _watchlists = new();
    private readonly ListChoice _allMarkets = new(null, "All markets");
    private ListChoice _listFilter;
    private StatusChoice? _statusBeforeList;

    /// <summary>Creates the page.</summary>
    /// <param name="format">How values and times look.</param>
    /// <param name="time">The clock the waits use.</param>
    /// <param name="context">Where the feed raises its events; the UI thread's context at <see cref="Attach"/> when omitted.</param>
    /// <param name="dialogs">Asks for a watchlist's name and for confirmation to delete one; without it those commands do nothing.</param>
    public MarketsViewModel(Formatting format, TimeProvider time, SynchronizationContext? context = null, IDialogs? dialogs = null)
    {
        _format = format;
        _time = time;
        _context = context;
        _dialogs = dialogs;
        _listFilter = _allMarkets;
        ListChoices = [_allMarkets];
        Menu = new WatchlistMenu(_watchlists, dialogs, ReportProblem);
        _watchlists.Changed += OnWatchlistsChanged;
        NewListCommand = new AsyncCommand(NewListAsync, ex => Error = ex.Message, () => _dialogs is not null && _api is not null);
        DeleteListCommand = new AsyncCommand(DeleteListAsync, ex => Error = ex.Message, () => _dialogs is not null && _listFilter.Name is not null);
        RefreshListsCommand = new AsyncCommand(LoadListsAsync, ex => Error = ex.Message, () => _api is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => HasFilters);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SearchText) or nameof(MinVolumeText) or nameof(StatusFilter) or nameof(ListFilter))
            {
                ClearFiltersCommand.RaiseCanExecuteChanged();
            }
        };
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

    /// <summary>"All markets" and then one choice for each watchlist.</summary>
    public ObservableCollection<ListChoice> ListChoices { get; }

    /// <summary>The menu that adds the selected market to a watchlist and removes it.</summary>
    public WatchlistMenu Menu { get; }

    public AsyncCommand NewListCommand { get; }

    public AsyncCommand DeleteListCommand { get; }

    public AsyncCommand RefreshListsCommand { get; }

    /// <summary>Puts the search, the status, the minimum volume and the list back to their starting values (the ranking is not a filter and stays).</summary>
    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>Whether any filter differs from its starting value.</summary>
    public bool HasFilters => _searchText.Length > 0 || _minVolumeText.Length > 0 || !ReferenceEquals(_status, Statuses[1]) || _listFilter.Name is not null;

    /// <summary>
    /// Which markets to list: all, or those in one watchlist. Choosing a list shows markets of every status (a list may hold finished ones that "Active" would hide); going back to all markets restores
    /// the status that was chosen before.
    /// </summary>
    public ListChoice ListFilter
    {
        get => _listFilter;
        set
        {
            if ((object?)value is null || !Set(ref _listFilter, value))
            {
                return;
            }

            if (value.Name is not null && _statusBeforeList is null)
            {
                _statusBeforeList = _status;
                _status = Statuses[0];
                Raise(nameof(StatusFilter));
            }
            else if (value.Name is null && _statusBeforeList is { } before)
            {
                _status = before;
                _statusBeforeList = null;
                Raise(nameof(StatusFilter));
            }

            DeleteListCommand.RaiseCanExecuteChanged();
            ReloadSoon(immediately: true);
        }
    }

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
                Menu.Ticker = value?.Ticker;
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

    private void ClearFilters()
    {
        _debounce?.Cancel();
        _searchText = string.Empty;
        _minVolumeText = string.Empty;
        _status = Statuses[1];
        _statusBeforeList = null;
        _listFilter = _allMarkets;
        Raise(nameof(SearchText));
        Raise(nameof(MinVolumeText));
        Raise(nameof(MinVolumeInvalid));
        Raise(nameof(StatusFilter));
        Raise(nameof(ListFilter));
        DeleteListCommand.RaiseCanExecuteChanged();
        ReloadSoon(immediately: true);
    }

    private string? EffectiveSearch => _searchText.Trim().Length >= 2 ? _searchText.Trim() : null;

    private string? MinVolume => MinVolumeShape().IsMatch(_minVolumeText.Trim()) ? _minVolumeText.Trim() : null;

    /// <summary>Points the page at a server: opens the live feed and clears the old list. The caller then loads the first page.</summary>
    /// <param name="create">Makes a client. Called twice: one for the page's own requests, one for the feed.</param>
    public void Attach(Func<IKalTermApi> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        Detach();
        _api = create();
        _watchlists.Attach(_api);
        NewListCommand.RaiseCanExecuteChanged();
        RefreshListsCommand.RaiseCanExecuteChanged();
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
        _watchlists.Attach(null);
        _listFilter = _allMarkets;
        Raise(nameof(ListFilter));
        if (_statusBeforeList is { } before)
        {
            _status = before;
            _statusBeforeList = null;
            Raise(nameof(StatusFilter));
        }

        NewListCommand.RaiseCanExecuteChanged();
        DeleteListCommand.RaiseCanExecuteChanged();
        RefreshListsCommand.RaiseCanExecuteChanged();
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

    public void Dispose()
    {
        Detach();
        _watchlists.Changed -= OnWatchlistsChanged;
        Menu.Dispose();
    }

    /// <summary>Loads the first page again (used when the ranking, a filter or the search changes, and to start).</summary>
    /// <returns>A task that completes when the page is shown or has failed.</returns>
    public async Task LoadAsync()
    {
        await FetchAsync(0, null).ConfigureAwait(true);
        if (!_watchlists.Loaded)
        {
            await LoadListsAsync().ConfigureAwait(true);   // after the page, so the page shows at once and a failure here is not wiped by it
        }
    }

    private async Task LoadListsAsync()
    {
        if (await _watchlists.LoadAsync().ConfigureAwait(true) is { } problem)
        {
            ReportProblem(problem);
        }
    }

    private void ReportProblem(string problem) => Error = problem;

    private async Task NewListAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        var name = await _dialogs.AskNameAsync("New watchlist", "A name for the new watchlist.", s => WatchlistService.NameProblem(s)).ConfigureAwait(true);
        if (name is not null && await _watchlists.CreateAsync(name).ConfigureAwait(true) is { } problem)
        {
            ReportProblem(problem);
        }
    }

    private async Task DeleteListAsync()
    {
        if (_dialogs is null || _listFilter.Name is not { } name)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync("Delete watchlist", $"Delete the watchlist {name}? The markets in it are not affected.").ConfigureAwait(true)
            && await _watchlists.DeleteAsync(name).ConfigureAwait(true) is { } problem)
        {
            ReportProblem(problem);
        }
    }

    private void OnWatchlistsChanged()
    {
        var present = _watchlists.Lists;
        for (var i = ListChoices.Count - 1; i >= 1; i--)
        {
            if (!present.Any(l => l.Name == ListChoices[i].Name))
            {
                ListChoices.RemoveAt(i);
            }
        }

        foreach (var info in present)
        {
            var text = $"{info.Name} ({info.Count})";
            var existing = ListChoices.FirstOrDefault(c => c.Name == info.Name);
            if (existing is null)
            {
                var at = 1;
                while (at < ListChoices.Count && string.CompareOrdinal(ListChoices[at].Name, info.Name) < 0)
                {
                    at++;
                }

                ListChoices.Insert(at, new ListChoice(info.Name, text));
            }
            else
            {
                existing.Text = text;
            }
        }

        foreach (var row in Rows)
        {
            row.SetLists(_watchlists.ListsOf(row.Ticker));
        }

        if (_listFilter.Name is { } viewed)
        {
            if (!present.Any(l => l.Name == viewed))
            {
                ListFilter = _allMarkets;
            }
            else if (Rows.Any(r => !_watchlists.IsMember(viewed, r.Ticker)))
            {
                _ = SafeAsync(RefreshAsync);   // a market was taken out of the list on show
            }
        }
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
        var menu = new WatchlistMenu(_watchlists, _dialogs, ReportProblem) { Ticker = row.Ticker };
        var detail = new MarketDetailViewModel(row.Ticker, _api, _feed, _format, _time, menu);
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
            var shown = await _api.GetScreenerAsync(new ScreenQuery(Sort.Key, _descending ? "desc" : "asc", StatusFilter.Value, MinVolume, PageSize, cursor, _listFilter.Name, EffectiveSearch), token).ConfigureAwait(true);
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
            model.SetLists(_watchlists.ListsOf(row.Ticker));
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
