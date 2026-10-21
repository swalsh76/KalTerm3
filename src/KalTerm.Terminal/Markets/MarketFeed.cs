using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.Terminal.Markets;

/// <summary>Whether live ticks are arriving.</summary>
internal enum FeedState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,

    /// <summary>Opening the stream.</summary>
    Connecting,

    /// <summary>The stream is open.</summary>
    Live,

    /// <summary>The stream dropped and is being opened again; no tick is missed, they are replayed.</summary>
    Reconnecting,
}

/// <summary>
/// The terminal's one market stream. Pages say which markets they want, for ticks (<see cref="SetWanted"/>) and for order books (<see cref="SetWantedBooks"/>), each by its own name; the feed watches the union on a
/// single stream, adding and removing markets live as the pages change their minds, and hands every tick to <see cref="Tick"/> and every book to <see cref="Book"/>. Events are raised on the UI thread when the feed is
/// started from it. A tick is delivered once, even across a reconnect.
/// </summary>
internal sealed class MarketFeed : IDisposable
{
    /// <summary>How many price levels each side of a book the stream carries (the depth is fixed when the stream opens; a page shows as many of them as it likes).</summary>
    public const int BookDepth = 50;

    private readonly IKalTermApi _api;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly Lock _sync = new();
    private readonly Dictionary<StreamKind, Dictionary<string, HashSet<string>>> _wanted = new() { [StreamKind.Ticks] = new(StringComparer.Ordinal), [StreamKind.Books] = new(StringComparer.Ordinal) };
    private readonly Dictionary<StreamKind, HashSet<string>> _applied = new() { [StreamKind.Ticks] = new(StringComparer.Ordinal), [StreamKind.Books] = new(StringComparer.Ordinal) };   // what the stream has been told to watch
    private readonly SemaphoreSlim _changes = new(1, 1);
    private MarketWatcher? _watcher;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the feed.</summary>
    /// <param name="api">Its own client; the feed disposes it.</param>
    /// <param name="time">The clock the reconnect waits use.</param>
    /// <param name="context">Where events are raised; the current context when started from the UI thread.</param>
    public MarketFeed(IKalTermApi api, TimeProvider time, SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(time);
        _api = api;
        _time = time;
        _context = context ?? SynchronizationContext.Current;
    }

    /// <summary>A ticker update arrived.</summary>
    public event Action<MarketTickDto>? Tick;

    /// <summary>A book arrived (the whole book: replace what you hold).</summary>
    public event Action<OrderBookDto>? Book;

    /// <summary>The state changed; the text says why when there is something to say.</summary>
    public event Action<FeedState, string>? StateChanged;

    /// <summary>The server cannot continue from where the feed was: whatever was loaded should be loaded again.</summary>
    public event Action? Reset;

    public FeedState State { get; private set; } = FeedState.Stopped;

    /// <summary>Allows the stream to open, which it does as soon as some market is wanted (and keeps it open from then on).</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        Launch();
    }

    private void Launch()
    {
        MarketWatcher watcher;
        string[] ticks;
        string[] books;
        CancellationToken token;
        lock (_sync)
        {
            if (!_started || _disposed || _run is not null)
            {
                return;
            }

            ticks = Union(StreamKind.Ticks);
            books = Union(StreamKind.Books);
            if (ticks.Length == 0 && books.Length == 0)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            token = _stop.Token;
            watcher = _watcher = new MarketWatcher(
                (request, streamToken) => _api.StreamMarketsAsync(request, streamToken),
                _time,
                (streamId, kind, ticker, add, changeToken) => add ? _api.AddStreamMarketAsync(streamId, kind, ticker, changeToken) : _api.RemoveStreamMarketAsync(streamId, kind, ticker, changeToken));
            _applied[StreamKind.Ticks].UnionWith(ticks);
            _applied[StreamKind.Books].UnionWith(books);
            _run = Task.CompletedTask;   // taken: a change made from here on is applied to the watcher, which has not begun yet
        }

        Post(() => Change(FeedState.Connecting, string.Empty));   // before the run begins: a refusal that comes at once must be the last word, not the first
        var run = RunAsync(watcher, ticks, books, token);
        lock (_sync)
        {
            if (!_disposed)
            {
                _run = run;
            }
        }
    }

    /// <summary>Says which markets one page wants ticks for (replacing what it wanted before). A market several pages want is watched once.</summary>
    /// <param name="owner">The page.</param>
    /// <param name="tickers">The markets.</param>
    public void SetWanted(string owner, IReadOnlyCollection<string> tickers) => Want(StreamKind.Ticks, owner, tickers);

    /// <summary>Says which markets one page wants order books for (replacing what it wanted before). Only the markets the server publishes books for have one.</summary>
    /// <param name="owner">The page.</param>
    /// <param name="tickers">The markets.</param>
    public void SetWantedBooks(string owner, IReadOnlyCollection<string> tickers) => Want(StreamKind.Books, owner, tickers);

    private void Want(StreamKind kind, string owner, IReadOnlyCollection<string> tickers)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(tickers);
        bool running;
        lock (_sync)
        {
            if (tickers.Count == 0)
            {
                _wanted[kind].Remove(owner);
            }
            else
            {
                _wanted[kind][owner] = new HashSet<string>(tickers, StringComparer.Ordinal);
            }

            running = _run is not null;
        }

        if (running)
        {
            _ = ApplyAsync();
        }
        else
        {
            Launch();
        }
    }

    /// <summary>Closes the stream and gives the client back.</summary>
    public void Dispose()
    {
        CancellationTokenSource? stop;
        lock (_sync)
        {
            _disposed = true;
            stop = _stop;
            _stop = null;
            _run = null;
            _watcher = null;
        }

        stop?.Cancel();
        stop?.Dispose();
        (_api as IDisposable)?.Dispose();
    }

    private string[] Union(StreamKind kind)
    {
        lock (_sync)
        {
            return [.. _wanted[kind].Values.SelectMany(set => set).Distinct(StringComparer.Ordinal)];
        }
    }

    /// <summary>Tells the stream what to add and drop so it watches exactly what is wanted now. Always worked out from the current wish, never replayed, so two quick changes cannot cross.</summary>
    private async Task ApplyAsync()
    {
        MarketWatcher? watcher;
        CancellationToken token;
        lock (_sync)
        {
            watcher = _watcher;
            token = _stop?.Token ?? CancellationToken.None;
        }

        if (watcher is null)
        {
            return;
        }

        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var kind in new[] { StreamKind.Ticks, StreamKind.Books })
            {
                await ApplyKindAsync(watcher, kind, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (ApiException ex)
        {
            Post(() => Change(State, ex.Message));
        }
        finally
        {
            try
            {
                _changes.Release();
            }
            catch (ObjectDisposedException)
            {
                // closed
            }
        }
    }

    private async Task ApplyKindAsync(MarketWatcher watcher, StreamKind kind, CancellationToken token)
    {
        var wanted = Union(kind).ToHashSet(StringComparer.Ordinal);
        string[] add;
        string[] drop;
        lock (_sync)
        {
            add = [.. wanted.Where(t => !_applied[kind].Contains(t))];
            drop = [.. _applied[kind].Where(t => !wanted.Contains(t))];
        }

        foreach (var ticker in drop)
        {
            await watcher.RemoveAsync(kind, ticker, token).ConfigureAwait(false);
            lock (_sync)
            {
                _applied[kind].Remove(ticker);
            }
        }

        foreach (var ticker in add)
        {
            try
            {
                await watcher.AddAsync(kind, ticker, token).ConfigureAwait(false);
                lock (_sync)
                {
                    _applied[kind].Add(ticker);
                }
            }
            catch (ApiException ex) when (ex.Code == "too_many_markets")
            {
                Post(() => Change(State, "too many markets are watched at once; some are not live"));
            }
        }
    }

    private async Task RunAsync(MarketWatcher watcher, string[] ticks, string[] books, CancellationToken token)
    {
        try
        {
            await watcher.RunAsync(
                ticks,
                books,
                BookDepth,
                item =>
                {
                    switch (item)
                    {
                        case ReadyEvent:
                            Post(() => Change(FeedState.Live, string.Empty));
                            break;
                        case TickEvent tick:
                            Post(() => Tick?.Invoke(tick.Tick));
                            break;
                        case BookEvent book:
                            Post(() => Book?.Invoke(book.Book));
                            break;
                        case ResetEvent:
                            Post(() => Reset?.Invoke());
                            break;
                    }

                    return Task.CompletedTask;
                },
                notice => Post(() => Change(FeedState.Reconnecting, notice)),
                token).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            Post(() => Change(FeedState.Stopped, ex.Message));
        }
    }

    private void Change(FeedState state, string note)
    {
        State = state;
        StateChanged?.Invoke(state, note);
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
