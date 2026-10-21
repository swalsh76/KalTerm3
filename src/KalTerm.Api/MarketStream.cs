using System.Globalization;
using System.Text;
using System.Threading.Channels;
using KalTerm.Contracts;
using KalTerm.Data.Auth;
using KalTerm.Data.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api;

/// <summary>The limits and timings of the market streams (ticker updates and order books pushed to a client).</summary>
public sealed record MarketStreamOptions
{
    /// <summary>The most market streams one user can have open. Default 5.</summary>
    public int MaxPerUser { get; init; } = 5;

    /// <summary>The most market streams the server can have open. Default 50.</summary>
    public int MaxTotal { get; init; } = 50;

    /// <summary>The most markets one stream can watch (ticks and books together, each market once). Default 50.</summary>
    public int MaxMarkets { get; init; } = 50;

    /// <summary>How often a quiet stream sends a keep-alive comment. Default 15 seconds.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a stream checks that its token is still good. Default 30 seconds.</summary>
    public TimeSpan TokenCheck { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the watcher reads the database while any stream is open. Default 1 second.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far behind now the ticker table is read. The ingest writes in batches that flush at least once a second, and a row can land just after rows with a later time; reading only what is at least this old means
    /// the rows of a moment are all there before they are read. Measured on the server (BUILD-LOG 9.4): a row is readable a median of 1.1 s after its time, 99% within 2 s, at most 2.15 s in three minutes; a 2-second margin lost
    /// about one tick in 300, so the margin is 3 seconds. Default 3 seconds.
    /// </summary>
    public TimeSpan TickLag { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The most ticker rows one poll reads. Default 20,000.</summary>
    public int PollRows { get; init; } = 20_000;

    /// <summary>The most ticker rows a reconnecting stream is replayed. Default 20,000.</summary>
    public int ReplayRows { get; init; } = 20_000;

    /// <summary>How far back a reconnecting stream can resume. Default 10 minutes.</summary>
    public TimeSpan MaxReplay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The events a stream can have waiting; a client that falls further behind is told to reload. Default 20,000.</summary>
    public int QueueCapacity { get; init; } = 20_000;

    /// <summary>How often a ticker that was not found is looked for again (a market can appear). Default 30 seconds.</summary>
    public TimeSpan UnknownRecheck { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The depth of a pushed book when the client does not say. Default 10.</summary>
    public int DefaultDepth { get; init; } = 10;

    /// <summary>The deepest book a client can ask for. Default 100.</summary>
    public int MaxDepth { get; init; } = 100;

    /// <summary>The most events written before one flush. Default 500.</summary>
    public int WriteBatch { get; init; } = 500;
}

/// <summary>Something pushed to a stream.</summary>
public abstract record StreamItem;

/// <summary>A ticker update.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Tick">The update.</param>
public sealed record TickItem(string Ticker, StoredTick Tick) : StreamItem;

/// <summary>A book that changed (the whole book; the client replaces what it has).</summary>
/// <param name="Book">The book.</param>
public sealed record BookItem(StoredBook Book) : StreamItem;

/// <summary>The stream's watch list changed (the confirmation, in order with the ticks and books).</summary>
/// <param name="Added">Whether the market was added (otherwise removed).</param>
/// <param name="Books">Whether it is the book subscription (otherwise the ticks).</param>
/// <param name="Ticker">The market.</param>
/// <param name="From">For a new ticks subscription, where its live ticks begin.</param>
/// <param name="Unknown">The ticker is not a market here.</param>
/// <param name="NoBook">The market has no published book.</param>
public sealed record SubscriptionItem(bool Added, bool Books, string Ticker, DateTime? From, bool Unknown, bool NoBook) : StreamItem;

/// <summary>What a live change of a stream's watch list came to.</summary>
public enum StreamChange
{
    /// <summary>It was applied (or nothing needed changing) and the stream will confirm it.</summary>
    Done,

    /// <summary>The stream is gone or is not the caller's.</summary>
    Gone,

    /// <summary>The stream would watch more markets than allowed.</summary>
    TooManyMarkets,
}

/// <summary>One open market stream: what it watches and the queue the watcher fills.</summary>
public sealed class MarketStreamSession : IDisposable
{
    private readonly Channel<StreamItem> _queue;
    private readonly MarketStreamHub _hub;
    private int _released;

    internal MarketStreamSession(MarketStreamHub hub, int userId, IReadOnlySet<string> ticks, IReadOnlySet<string> books, DateTime joinCursor, int capacity)
    {
        _hub = hub;
        UserId = userId;
        TickSet = new HashSet<string>(ticks, StringComparer.Ordinal);
        BookSet = new HashSet<string>(books, StringComparer.Ordinal);
        JoinCursor = joinCursor;
        Id = Guid.NewGuid().ToString("N")[..16];
        _queue = Channel.CreateBounded<StreamItem>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    /// <summary>The stream's id.</summary>
    public string Id { get; }

    /// <summary>The user.</summary>
    public int UserId { get; }

    /// <summary>The markets whose ticks are pushed. Changed only through <see cref="MarketStreamHub.ChangeAsync"/>, between two polls.</summary>
    public IReadOnlySet<string> Ticks => TickSet;

    /// <summary>The markets whose books are pushed. Changed only through <see cref="MarketStreamHub.ChangeAsync"/>, between two polls.</summary>
    public IReadOnlySet<string> Books => BookSet;

    internal HashSet<string> TickSet { get; }

    internal HashSet<string> BookSet { get; }

    /// <summary>How many markets the stream watches (a market watched for both ticks and books counts once).</summary>
    internal int MarketCount => TickSet.Count + BookSet.Count(b => !TickSet.Contains(b));

    /// <summary>Live ticks begin here: everything from this moment on arrives through <see cref="Reader"/>; anything earlier is the client's to replay.</summary>
    public DateTime JoinCursor { get; }

    /// <summary>What is waiting to be written to the client.</summary>
    public ChannelReader<StreamItem> Reader => _queue.Reader;

    /// <summary>Whether the client fell too far behind and the queue was closed.</summary>
    public bool Overflowed { get; private set; }

    internal void Push(StreamItem item)
    {
        if (Overflowed)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(item))
        {
            Overflowed = true;
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>Gives the place back and stops the queue.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _queue.Writer.TryComplete();
            _hub.Release(this);
        }
    }
}

/// <summary>
/// Keeps the market streams. One watcher reads the database once a second while any stream is open: the books of the markets some stream watches (a book is sent when it changed) and, in one query for all of them, the
/// ticker rows of those markets since the last poll, up to <see cref="MarketStreamOptions.TickLag"/> behind now. What is read is handed to the streams that watch it. With no stream open nothing is read.
/// </summary>
public sealed partial class MarketStreamHub : IDisposable
{
    private readonly IMarketStreamReader _reader;
    private readonly IBookReader _books;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // a stream is added between polls, never during one, so no moment of ticks is missed by it
    private readonly List<MarketStreamSession> _sessions = [];
    private readonly Dictionary<int, int> _perUser = [];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _unknownSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Updated, bool Stale, bool Approximate)> _lastBook = new(StringComparer.Ordinal);
    private DateTime? _cursor;

    /// <summary>Creates the hub.</summary>
    /// <param name="reader">Reads markets and ticker rows.</param>
    /// <param name="books">Reads books.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The limits.</param>
    /// <param name="logger">Receives the log.</param>
    public MarketStreamHub(IMarketStreamReader reader, IBookReader books, TimeProvider time, MarketStreamOptions? options = null, ILogger<MarketStreamHub>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(time);
        _reader = reader;
        _books = books;
        _time = time;
        Options = options ?? new MarketStreamOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketStreamHub>.Instance;
    }

    /// <summary>The limits and timings.</summary>
    public MarketStreamOptions Options { get; }

    /// <summary>How many streams are open.</summary>
    public int Open
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Opens a stream for a user, between two polls.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="ticks">The markets whose ticks are wanted.</param>
    /// <param name="books">The markets whose books are wanted.</param>
    /// <param name="cancellationToken">Cancels the wait for the gap between polls.</param>
    /// <returns>The stream (dispose it when it ends), or <see langword="null"/> when the user or the server is at the limit.</returns>
    public async Task<MarketStreamSession?> TryOpenAsync(int userId, IReadOnlySet<string> ticks, IReadOnlySet<string> books, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentNullException.ThrowIfNull(books);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_sessions.Count >= Options.MaxTotal || _perUser.GetValueOrDefault(userId) >= Options.MaxPerUser)
                {
                    return null;
                }

                _cursor ??= Floor(_time.GetUtcNow().UtcDateTime - Options.TickLag);
                var session = new MarketStreamSession(this, userId, ticks, books, _cursor.Value, Options.QueueCapacity);
                _sessions.Add(session);
                _perUser[userId] = _perUser.GetValueOrDefault(userId) + 1;
                return session;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads what the open streams watch and hands it to them. A failed read is logged and tried again at the next poll; the streams lose nothing.</summary>
    /// <param name="cancellationToken">Cancels the poll.</param>
    /// <returns>A task that completes when the poll is done.</returns>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarketStreamSession[] sessions;
            DateTime cursor;
            lock (_sync)
            {
                if (_sessions.Count == 0)
                {
                    _cursor = null;   // nothing was watching: the next stream starts from its own now
                    return;
                }

                sessions = [.. _sessions];
                cursor = _cursor!.Value;
            }

            try
            {
                await PollBooksAsync(sessions, cancellationToken).ConfigureAwait(false);
                var next = await PollTicksAsync(sessions, cursor, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _cursor = next;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogReadFailed(_logger, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a market to, or removes it from, what an open stream watches, between two polls. The stream confirms it in order with its ticks and books: everything queued before the confirmation still arrives, and nothing for a removed
    /// market after it. A new ticks subscription gets every tick from the confirmation's <c>From</c> on; a new books subscription gets the current book at once.
    /// </summary>
    /// <param name="userId">The caller; only the stream's own user can change it.</param>
    /// <param name="streamId">The stream.</param>
    /// <param name="books">The book subscription (otherwise the ticks).</param>
    /// <param name="ticker">The market.</param>
    /// <param name="add">Add (otherwise remove).</param>
    /// <param name="cancellationToken">Cancels the wait for the gap between polls.</param>
    /// <returns>What came of it.</returns>
    public async Task<StreamChange> ChangeAsync(int userId, string streamId, bool books, string ticker, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(ticker);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarketStreamSession? session;
            bool already;
            lock (_sync)
            {
                session = _sessions.Find(s => s.UserId == userId && string.Equals(s.Id, streamId, StringComparison.Ordinal));
                if (session is null)
                {
                    return StreamChange.Gone;
                }

                var set = books ? session.BookSet : session.TickSet;
                already = set.Contains(ticker);
                if (add && !already && session.MarketCount >= Options.MaxMarkets && !(books ? session.TickSet : session.BookSet).Contains(ticker))
                {
                    return StreamChange.TooManyMarkets;
                }
            }

            if (!add)
            {
                lock (_sync)
                {
                    (books ? session.BookSet : session.TickSet).Remove(ticker);
                }

                session.Push(new SubscriptionItem(false, books, ticker, null, false, false));
                return StreamChange.Done;
            }

            var unknown = !(await _reader.ResolveAsync([ticker], cancellationToken).ConfigureAwait(false)).ContainsKey(ticker);
            StoredBook? book = null;
            if (books && !already && !unknown)
            {
                var found = await _books.GetManyAsync([ticker], cancellationToken).ConfigureAwait(false);
                book = found.Count > 0 ? found[0] : null;
            }

            DateTime? from = null;
            lock (_sync)
            {
                if (!_sessions.Contains(session))
                {
                    return StreamChange.Gone;
                }

                if (!already)
                {
                    (books ? session.BookSet : session.TickSet).Add(ticker);
                    from = books ? null : _cursor ?? Floor(_time.GetUtcNow().UtcDateTime - Options.TickLag);
                }
            }

            session.Push(new SubscriptionItem(true, books, ticker, from, unknown, books && !unknown && !already && book is null));
            if (book is not null)
            {
                session.Push(new BookItem(book));
            }

            return StreamChange.Done;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    internal void Release(MarketStreamSession session)
    {
        lock (_sync)
        {
            if (!_sessions.Remove(session))
            {
                return;
            }

            if (_perUser.TryGetValue(session.UserId, out var count))
            {
                if (count <= 1)
                {
                    _perUser.Remove(session.UserId);
                }
                else
                {
                    _perUser[session.UserId] = count - 1;
                }
            }
        }
    }

    private static DateTime Floor(DateTime moment) => new(moment.Ticks - (moment.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    private async Task PollBooksAsync(MarketStreamSession[] sessions, CancellationToken cancellationToken)
    {
        var tickers = sessions.SelectMany(s => s.Books).Distinct(StringComparer.Ordinal).ToList();
        if (tickers.Count == 0)
        {
            return;
        }

        var books = await _books.GetManyAsync(tickers, cancellationToken).ConfigureAwait(false);
        foreach (var book in books)
        {
            var state = (book.UpdatedAt, book.Stale, book.Approximate);
            if (_lastBook.TryGetValue(book.Ticker, out var last) && last == state)
            {
                continue;
            }

            _lastBook[book.Ticker] = state;
            var item = new BookItem(book);
            foreach (var session in sessions.Where(s => s.Books.Contains(book.Ticker)))
            {
                session.Push(item);
            }
        }
    }

    private async Task<DateTime> PollTicksAsync(MarketStreamSession[] sessions, DateTime cursor, CancellationToken cancellationToken)
    {
        var until = Floor(_time.GetUtcNow().UtcDateTime - Options.TickLag);
        var tickers = sessions.SelectMany(s => s.Ticks).Distinct(StringComparer.Ordinal).ToList();
        if (tickers.Count == 0 || until <= cursor)
        {
            return until > cursor ? until : cursor;
        }

        await ResolveAsync(tickers, cancellationToken).ConfigureAwait(false);
        var markets = new Dictionary<int, string>();
        foreach (var ticker in tickers)
        {
            if (_ids.TryGetValue(ticker, out var id))
            {
                markets[id] = ticker;
            }
        }

        if (markets.Count == 0)
        {
            return until;
        }

        var rows = await _reader.ReadTicksAsync(markets, cursor, until, Options.PollRows, cancellationToken).ConfigureAwait(false);
        var next = until;
        if (rows.Count >= Options.PollRows)
        {
            // The read was cut off: the rows of the last millisecond may be incomplete, so they are left for the next poll (unless that is all there is, which is passed on as it is).
            var last = rows[^1].Tick.Ts;
            var complete = rows.TakeWhile(r => r.Tick.Ts < last).ToList();
            if (complete.Count > 0)
            {
                rows = complete;
                next = last;
            }
            else
            {
                next = last.AddMilliseconds(1);
            }

            LogCutOff(_logger, rows.Count);
        }

        foreach (var row in rows)
        {
            var item = new TickItem(row.Ticker, row.Tick);
            foreach (var session in sessions)
            {
                if (session.Ticks.Contains(row.Ticker))
                {
                    session.Push(item);
                }
            }
        }

        return next;
    }

    private async Task ResolveAsync(List<string> tickers, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var need = tickers.Where(t => !_ids.ContainsKey(t) && (!_unknownSince.TryGetValue(t, out var since) || now - since >= Options.UnknownRecheck)).ToList();
        if (need.Count == 0)
        {
            return;
        }

        var found = await _reader.ResolveAsync(need, cancellationToken).ConfigureAwait(false);
        foreach (var ticker in need)
        {
            if (found.TryGetValue(ticker, out var id))
            {
                _ids[ticker] = id;
                _unknownSince.Remove(ticker);
            }
            else
            {
                _unknownSince[ticker] = now;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "could not read for the market streams: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "a market stream poll hit its row limit; {Rows} rows were handed on and the rest is read next")]
    private static partial void LogCutOff(ILogger logger, int rows);
}

/// <summary>Runs <see cref="MarketStreamHub.PollAsync"/> for as long as the server runs.</summary>
/// <param name="hub">The hub.</param>
public sealed class MarketStreamWatcher(MarketStreamHub hub) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(hub.Options.Poll);
        try
        {
            do
            {
                await hub.PollAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}

/// <summary>The market stream endpoint: Server-Sent Events of ticker updates and order books of the markets the caller names.</summary>
internal static class MarketStreamEndpoint
{
    private const string MomentFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static async Task<IResult> HandleAsync(HttpContext context, MarketStreamHub hub, IMarketStreamReader reader, IBookReader books, TimeProvider time)
    {
        if (context.Items[ApiAuthentication.UserItem] is not AuthenticatedUser user)
        {
            return ApiEndpoints.Json(new ErrorDto("unauthorized"), ApiJson.Wire.ErrorDto, StatusCodes.Status401Unauthorized);
        }

        var options = hub.Options;
        var parameters = new RequestParameters(context.Request.Query);
        var ticks = Tickers(context.Request.Query, "ticks", parameters);
        var bookTickers = Tickers(context.Request.Query, "books", parameters);
        var depth = parameters.Depth(options.DefaultDepth, options.MaxDepth);
        var resume = Resume(context, parameters);
        if (ticks.Count + bookTickers.Except(ticks).Count() > options.MaxMarkets)
        {
            parameters.Reject("ticks", "books");
        }

        if (ticks.Count == 0 && bookTickers.Count == 0)
        {
            parameters.Reject("ticks", "books");
        }

        if (!parameters.IsValid)
        {
            return ApiEndpoints.Invalid([.. parameters.Invalid]);
        }

        var session = await hub.TryOpenAsync(user.UserId, ticks, bookTickers, context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            context.Response.Headers.RetryAfter = "30";
            return ApiEndpoints.Json(new ErrorDto("too_many_streams"), ApiJson.Wire.ErrorDto, StatusCodes.Status429TooManyRequests);
        }

        using (session)
        {
            var aborted = context.RequestAborted;
            try
            {
                var all = ticks.Union(bookTickers).ToHashSet(StringComparer.Ordinal);
                var ids = await reader.ResolveAsync(all, aborted).ConfigureAwait(false);
                var known = ticks.Where(ids.ContainsKey).ToList();
                var knownBooks = bookTickers.Where(ids.ContainsKey).ToList();
                var initial = await books.GetManyAsync(knownBooks, aborted).ConfigureAwait(false);
                var withBook = initial.Select(b => b.Ticker).ToHashSet(StringComparer.Ordinal);
                var ready = new StreamReadyDto(
                    session.Id,
                    session.JoinCursor.ToString(MomentFormat, CultureInfo.InvariantCulture),
                    [.. known.Order(StringComparer.Ordinal)],
                    [.. knownBooks.Order(StringComparer.Ordinal)],
                    [.. all.Where(t => !ids.ContainsKey(t)).Order(StringComparer.Ordinal)],
                    [.. knownBooks.Where(t => !withBook.Contains(t)).Order(StringComparer.Ordinal)],
                    depth);

                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await context.Response.StartAsync(aborted).ConfigureAwait(false);
                await WriteAsync(context, Event("ready", ready.ResumeFrom, ApiJson.Serialize(ready)), aborted).ConfigureAwait(false);

                var markets = known.ToDictionary(t => ids[t], t => t);
                if (resume is { } from && !await ReplayAsync(context, reader, options, markets, from, session.JoinCursor, aborted).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                var sent = new Dictionary<string, (DateTime Updated, bool Stale, bool Approximate)>(StringComparer.Ordinal);
                var now = time.GetUtcNow().UtcDateTime;
                var opening = new StringBuilder();
                foreach (var book in initial)
                {
                    sent[book.Ticker] = (book.UpdatedAt, book.Stale, book.Approximate);
                    opening.Append(BookEvent(book, now, depth));
                }

                if (opening.Length > 0)
                {
                    await WriteAsync(context, opening.ToString(), aborted).ConfigureAwait(false);
                }

                await RunAsync(context, session, sent, depth, user, time, aborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                // The client went away.
            }
        }

        return Results.Empty;
    }

    /// <summary>Adds a market to, or removes it from, an open stream of the caller's. The stream confirms it.</summary>
    public static async Task<IResult> ChangeAsync(HttpContext context, MarketStreamHub hub, string streamId, string ticker, bool books, bool add)
    {
        if (context.Items[ApiAuthentication.UserItem] is not AuthenticatedUser user)
        {
            return ApiEndpoints.Json(new ErrorDto("unauthorized"), ApiJson.Wire.ErrorDto, StatusCodes.Status401Unauthorized);
        }

        if (!RequestParameters.IsTicker(ticker))
        {
            return ApiEndpoints.Invalid("ticker");
        }

        return await hub.ChangeAsync(user.UserId, streamId, books, ticker, add, context.RequestAborted).ConfigureAwait(false) switch
        {
            StreamChange.Done => Results.NoContent(),
            StreamChange.TooManyMarkets => ApiEndpoints.Json(new ErrorDto("too_many_markets"), ApiJson.Wire.ErrorDto, StatusCodes.Status409Conflict),
            _ => ApiEndpoints.NotFound(),
        };
    }

    private static async Task RunAsync(HttpContext context, MarketStreamSession session, Dictionary<string, (DateTime Updated, bool Stale, bool Approximate)> sent, int depth, AuthenticatedUser user, TimeProvider time, CancellationToken aborted)
    {
        var options = context.RequestServices.GetRequiredService<MarketStreamHub>().Options;
        var token = ApiAuthentication.BearerToken(context.Request);
        var nextTokenCheck = time.GetUtcNow() + options.TokenCheck;
        var text = new StringBuilder();
        Task<StreamItem>? pending = null;   // one read at a time: a read that timed out for a keep-alive is kept, not abandoned (an abandoned read would swallow the next item)
        while (!aborted.IsCancellationRequested)
        {
            try
            {
                pending ??= session.Reader.ReadAsync(aborted).AsTask();
                var first = await pending.WaitAsync(options.KeepAlive, aborted).ConfigureAwait(false);
                pending = null;
                text.Clear();
                var written = 0;
                var item = first;
                while (true)
                {
                    Append(text, item, sent, depth, time.GetUtcNow().UtcDateTime);
                    if (++written >= options.WriteBatch || !session.Reader.TryRead(out var more))
                    {
                        break;
                    }

                    item = more;
                }

                await WriteAsync(context, text.ToString(), aborted).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await WriteAsync(context, ": keep-alive\n\n", aborted).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                pending = null;
                if (session.Overflowed)
                {
                    await WriteAsync(context, Event("reset", null, ApiJson.Serialize(new StreamResetDto("slow_consumer"))), aborted).ConfigureAwait(false);
                }

                return;
            }

            if (token is not null && time.GetUtcNow() >= nextTokenCheck)
            {
                nextTokenCheck = time.GetUtcNow() + options.TokenCheck;
                var current = await context.RequestServices.GetRequiredService<TokenAuthenticator>().AuthenticateAsync(token, aborted).ConfigureAwait(false);
                if (current is null || current.UserId != user.UserId)
                {
                    return;   // revoked, expired or disabled: the stream ends and the next connect is refused
                }
            }
        }
    }

    private static void Append(StringBuilder text, StreamItem item, Dictionary<string, (DateTime Updated, bool Stale, bool Approximate)> sent, int depth, DateTime now)
    {
        switch (item)
        {
            case TickItem tick:
                text.Append(Event("tick", tick.Tick.Ts.ToString(MomentFormat, CultureInfo.InvariantCulture), ApiJson.Serialize(new MarketTickDto(tick.Ticker, Mapping.ToDto(tick.Tick)))));
                break;
            case BookItem book when !sent.TryGetValue(book.Book.Ticker, out var last) || (book.Book.UpdatedAt >= last.Updated && (book.Book.UpdatedAt, book.Book.Stale, book.Book.Approximate) != last):
                sent[book.Book.Ticker] = (book.Book.UpdatedAt, book.Book.Stale, book.Book.Approximate);
                text.Append(BookEvent(book.Book, now, depth));
                break;
            case SubscriptionItem change:
                if (change.Books)
                {
                    sent.Remove(change.Ticker);   // a book subscribed again is sent afresh, whatever was sent before
                }

                text.Append(Event(
                    change.Added ? "subscribed" : "unsubscribed",
                    null,
                    ApiJson.Serialize(new StreamSubscriptionDto(change.Books ? "books" : "ticks", change.Ticker, change.From?.ToString(MomentFormat, CultureInfo.InvariantCulture), change.Unknown, change.NoBook))));
                break;
        }
    }

    /// <summary>Sends the ticks the client missed. Returns false when the stream was ended with a reset.</summary>
    private static async Task<bool> ReplayAsync(HttpContext context, IMarketStreamReader reader, MarketStreamOptions options, Dictionary<int, string> markets, DateTime from, DateTime join, CancellationToken aborted)
    {
        if (from >= join || markets.Count == 0)
        {
            return true;
        }

        if (join - from > options.MaxReplay)
        {
            await WriteAsync(context, Event("reset", null, ApiJson.Serialize(new StreamResetDto("too_far_behind"))), aborted).ConfigureAwait(false);
            return false;
        }

        var rows = await reader.ReadTicksAsync(markets, from, join, options.ReplayRows + 1, aborted).ConfigureAwait(false);
        if (rows.Count > options.ReplayRows)
        {
            await WriteAsync(context, Event("reset", null, ApiJson.Serialize(new StreamResetDto("too_far_behind"))), aborted).ConfigureAwait(false);
            return false;
        }

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(Event("tick", row.Tick.Ts.ToString(MomentFormat, CultureInfo.InvariantCulture), ApiJson.Serialize(new MarketTickDto(row.Ticker, Mapping.ToDto(row.Tick)))));
            if (text.Length > 64_000)
            {
                await WriteAsync(context, text.ToString(), aborted).ConfigureAwait(false);
                text.Clear();
            }
        }

        if (text.Length > 0)
        {
            await WriteAsync(context, text.ToString(), aborted).ConfigureAwait(false);
        }

        return true;
    }

    private static HashSet<string> Tickers(IQueryCollection query, string name, RequestParameters parameters)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!query.TryGetValue(name, out var values))
        {
            return result;
        }

        if (values.Count != 1)
        {
            parameters.Reject(name);
            return result;
        }

        foreach (var ticker in values[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (RequestParameters.IsTicker(ticker))
            {
                result.Add(ticker);
            }
            else
            {
                parameters.Reject(name);
            }
        }

        return result;
    }

    /// <summary>Where to resume: the <c>after</c> parameter, else the <c>Last-Event-ID</c> header (a tick's id, which is its time).</summary>
    private static DateTime? Resume(HttpContext context, RequestParameters parameters)
    {
        string? text = null;
        if (context.Request.Query.TryGetValue("after", out var after))
        {
            text = after.Count == 1 ? after[0] : null;
            if (text is null)
            {
                parameters.Reject("after");
                return null;
            }
        }
        else if (context.Request.Headers["Last-Event-ID"].Count == 1)
        {
            text = context.Request.Headers["Last-Event-ID"][0];
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateTime.TryParseExact(text, MomentFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment))
        {
            return moment;
        }

        parameters.Reject("after");
        return null;
    }

    private static string BookEvent(StoredBook book, DateTime now, int depth) => Event("book", null, ApiJson.Serialize(Mapping.ToDto(book, now, depth)));

    private static string Event(string type, string? id, string data) => (id is null ? string.Empty : $"id: {id}\n") + $"event: {type}\ndata: {data}\n\n";

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken cancellationToken)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
