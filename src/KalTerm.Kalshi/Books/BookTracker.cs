using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;

namespace KalTerm.Kalshi.Books;

/// <summary>The kinds of <see cref="BookEvent"/>.</summary>
public static class BookEventKinds
{
    /// <summary>A snapshot replaced a market's book.</summary>
    public const string Snapshot = "snapshot";

    /// <summary>A delta changed a market's book.</summary>
    public const string Delta = "delta";

    /// <summary>Messages were missed (or a delta made no sense): the named books are stale until a fresh snapshot.</summary>
    public const string Gap = "gap";

    /// <summary>The connection was replaced: every book and the sequence state were cleared.</summary>
    public const string Reset = "reset";

    /// <summary>The order book subscription was not restored after a reconnect (raised by the feed).</summary>
    public const string ResubscribeFailed = "resubscribe_failed";

    /// <summary>A market's book could not be recovered after a gap (raised by the feed).</summary>
    public const string ResyncFailed = "resync_failed";

    /// <summary>A message that is not about order books, passed through.</summary>
    public const string Message = "message";
}

/// <summary>One thing that happened to the books.</summary>
/// <param name="Kind">One of <see cref="BookEventKinds"/>.</param>
/// <param name="Ticker">The market, when the event is about one.</param>
/// <param name="Book">On a snapshot, a copy of the book as it was then; on a delta, the live book (which may already include later deltas).</param>
/// <param name="Tickers">The markets a gap affected.</param>
/// <param name="Message">The message behind the event.</param>
/// <param name="Detail">Why, for a gap or a failure.</param>
public sealed record BookEvent(
    string Kind,
    string? Ticker = null,
    LocalOrderBook? Book = null,
    IReadOnlyList<string>? Tickers = null,
    WsMessage? Message = null,
    string Detail = "")
{
    /// <summary>The markets a gap affected (empty for other events).</summary>
    public IReadOnlyList<string> AffectedTickers { get; } = Tickers ?? [];
}

/// <summary>
/// The order book state machine: feed it the stream's messages, it applies them and says what happened. It is a port of the reference's <c>BookTracker</c>
/// (golden vectors replay the same message scripts through both).
/// <para>
/// Sequence numbers are one counter per subscription, shared by all its markets and starting at 1; a snapshot, a delta and a command reply (the stream's
/// <c>control</c> marker) each use one. A skipped number invalidates every book of that subscription: its markets go stale, a <see cref="BookEventKinds.Gap"/>
/// is reported, and a stale book ignores deltas until a fresh snapshot arrives. A reconnect clears everything (sequence numbers and subscription ids restart).
/// Not thread-safe: one consumer drives it.
/// </para>
/// </summary>
public sealed class BookTracker
{
    private const string SnapshotType = "orderbook_snapshot";
    private const string DeltaType = "orderbook_delta";

    private readonly Dictionary<string, LocalOrderBook> _books = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private readonly Dictionary<int, SubscriptionState> _subscriptions = [];
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    /// <summary>The current books by ticker.</summary>
    public IReadOnlyDictionary<string, LocalOrderBook> Books => _books;

    /// <summary>The markets whose books cannot be trusted until a fresh snapshot.</summary>
    public IReadOnlySet<string> Stale => _stale;

    /// <summary>
    /// Declares a new subscription: no books yet, and its first message must be sequence 1. Messages can overtake this call; then the subscription is already
    /// known and what they established (sequence position, books) is kept.
    /// </summary>
    /// <param name="sid">The subscription id.</param>
    /// <param name="tickers">Its markets.</param>
    public void BeginSubscription(int sid, IEnumerable<string> tickers)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        var list = tickers.ToList();
        if (_subscriptions.TryGetValue(sid, out var known))
        {
            known.Tickers.UnionWith(list);
            _stale.UnionWith(list.Where(t => !_books.ContainsKey(t)));
        }
        else
        {
            _subscriptions[sid] = new SubscriptionState(1, list);
            _stale.UnionWith(list);
        }

        _removed.ExceptWith(list);
    }

    /// <summary>Forgets a subscription's sequence state.</summary>
    /// <param name="sid">The subscription id.</param>
    public void EndSubscription(int sid) => _subscriptions.Remove(sid);

    /// <summary>Markets were added to a live subscription: no book until a snapshot arrives.</summary>
    /// <param name="sid">The subscription id.</param>
    /// <param name="tickers">The new markets.</param>
    public void BeginMarkets(int sid, IEnumerable<string> tickers)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        var list = tickers.ToList();
        if (_subscriptions.TryGetValue(sid, out var subscription))
        {
            subscription.Tickers.UnionWith(list);
        }

        _stale.UnionWith(list);
        _removed.ExceptWith(list);
    }

    /// <summary>Markets were removed from a live subscription: their books are dropped and late messages for them are counted in the sequence but ignored.</summary>
    /// <param name="sid">The subscription id.</param>
    /// <param name="tickers">The removed markets.</param>
    public void ForgetMarkets(int sid, IEnumerable<string> tickers)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        var list = tickers.ToList();
        if (_subscriptions.TryGetValue(sid, out var subscription))
        {
            subscription.Tickers.ExceptWith(list);
        }

        foreach (var ticker in list)
        {
            _books.Remove(ticker);
            _stale.Remove(ticker);
        }

        _removed.UnionWith(list);
    }

    /// <summary>
    /// Feeds one stream message to the books. A <c>reconnected</c> message clears everything; a <c>control</c> marker is counted in the sequence; other
    /// message types pass through; a snapshot replaces its market's book and a delta is applied to it, after the sequence check.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>What happened, possibly nothing.</returns>
    /// <exception cref="KalshiResponseException">An order book message whose payload cannot be read.</exception>
    public IReadOnlyList<BookEvent> Process(WsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type == KalshiWebSocket.ReconnectedType)
        {
            _books.Clear();
            _stale.Clear();
            _subscriptions.Clear();   // sequence numbers and subscription ids restart on a new connection
            _removed.Clear();
            return [new BookEvent(BookEventKinds.Reset, Message: message)];
        }

        if (message.Type == KalshiWebSocket.ControlType)
        {
            return CountControl(message);
        }

        if (message.Type is not (SnapshotType or DeltaType))
        {
            return [new BookEvent(BookEventKinds.Message, Message: message)];
        }

        var payload = message.Payload();
        var ticker = payload switch
        {
            OrderbookSnapshotMsg snapshot => snapshot.MarketTicker,
            OrderbookDeltaMsg delta => delta.MarketTicker,
            _ => throw new KalshiResponseException($"{message.Type}: unexpected payload"),
        };

        var events = new List<BookEvent>();
        var sid = message.Sid ?? 0;
        if (!_subscriptions.TryGetValue(sid, out var subscription))
        {
            subscription = new SubscriptionState(message.Seq is { } first and not 0 ? first : 1, []);
            _subscriptions[sid] = subscription;
        }

        var removed = _removed.Contains(ticker);
        if (!removed)
        {
            subscription.Tickers.Add(ticker);
        }

        if (message.Seq != subscription.Expected)
        {
            events.Add(Gap(subscription, message, removed ? null : ticker));
        }

        if (message.Seq is { } seq)
        {
            subscription.Expected = seq + 1;
        }

        if (removed)
        {
            return events;   // a late message for a market we dropped: counted in sequence, ignored
        }

        switch (payload)
        {
            case OrderbookSnapshotMsg snapshot:
                var book = LocalOrderBook.FromSnapshot(snapshot);
                _books[ticker] = book;
                _stale.Remove(ticker);
                events.Add(new BookEvent(BookEventKinds.Snapshot, ticker, book.Copy(), Message: message));
                break;
            case OrderbookDeltaMsg delta when !_stale.Contains(ticker) && _books.TryGetValue(ticker, out var live):
                if (live.ApplyDelta(delta))
                {
                    events.Add(new BookEvent(BookEventKinds.Delta, ticker, live, Message: message));
                }
                else
                {
                    _stale.Add(ticker);
                    events.Add(new BookEvent(BookEventKinds.Gap, Tickers: [ticker], Detail: "negative quantity"));
                }

                break;
            default:
                break;   // a delta for a stale market, or one with no book yet: counted in sequence, ignored
        }

        return events;
    }

    /// <summary>Replaces a market's book with a REST snapshot: the fallback when an in-stream snapshot is unavailable. The book is approximate.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="restBook">The REST order book.</param>
    /// <returns>The snapshot event.</returns>
    public BookEvent ApplyRestSnapshot(string ticker, Models.OrderBook restBook)
    {
        var book = LocalOrderBook.FromRest(ticker, restBook);
        _books[ticker] = book;
        _stale.Remove(ticker);
        return new BookEvent(BookEventKinds.Snapshot, ticker, book.Copy(), Detail: "rest");
    }

    /// <summary>A command reply used one of a subscription's sequence numbers: count it, and flag a gap if it is out of step.</summary>
    private List<BookEvent> CountControl(WsMessage message)
    {
        if (!_subscriptions.TryGetValue(message.Sid ?? 0, out var subscription) || message.Seq is not { } seq)
        {
            return [];
        }

        var events = new List<BookEvent>();
        if (seq != subscription.Expected)
        {
            events.Add(Gap(subscription, message, null));
        }

        subscription.Expected = seq + 1;
        return events;
    }

    private BookEvent Gap(SubscriptionState subscription, WsMessage message, string? extra)
    {
        var affected = subscription.Tickers.Concat(extra is null ? [] : [extra]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        _stale.UnionWith(affected);
        var seen = message.Seq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None";
        return new BookEvent(BookEventKinds.Gap, Tickers: affected, Detail: $"seq {seen}, expected {subscription.Expected}");
    }

    private sealed class SubscriptionState(long expected, IEnumerable<string> tickers)
    {
        public long Expected { get; set; } = expected;

        public HashSet<string> Tickers { get; } = new(tickers, StringComparer.Ordinal);
    }
}
