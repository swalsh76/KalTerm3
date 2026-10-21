using System.Collections.ObjectModel;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Layouts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>Which side of the book a row is on.</summary>
internal enum BookSide
{
    /// <summary>Someone offers to sell yes at this price (derived from the no bids: ask = 1 minus a no bid).</summary>
    Ask,

    /// <summary>Someone bids to buy yes at this price.</summary>
    Bid,
}

/// <summary>One price level of the ladder, as shown.</summary>
/// <param name="Side">Ask or bid.</param>
/// <param name="Price">The yes price in dollars.</param>
/// <param name="Size">The contracts resting at that price.</param>
/// <param name="Total">The contracts resting at that price and every better one (the depth up to here).</param>
/// <param name="Fraction">The total as a part of the larger side's total, for the bar behind the row (a drawing size, not a quantity).</param>
internal sealed record BookRow(BookSide Side, string Price, string Size, string Total, double Fraction);

/// <summary>
/// The live order book of one market as a ladder: the asks above (the farthest first, the best just above the spread), the bids below (the best first), with the spread and the middle in between. Only the markets
/// the server publishes a book for have one (the watch list); for any other the page says so. The book is read once over REST and then replaced by each whole book the stream sends; the server says when a book
/// cannot be trusted (messages missed, or rebuilt from a snapshot) and the page says so too. Every number is worked out from the exact decimal text, in whole millionths of a dollar and hundredths of a contract.
/// </summary>
internal sealed class OrderBookViewModel : ObservableObject, IDisposable
{
    /// <summary>How many levels each side can show.</summary>
    public static readonly IReadOnlyList<int> Depths = [10, 20, 50];

    private const string FeedOwner = "book";

    private readonly string _ticker;
    private readonly IKalTermApi _api;
    private readonly MarketFeed _feed;
    private readonly Formatting _format;
    private OrderBookDto? _last;
    private readonly ViewPreferences? _preferences;
    private int _depth = 10;
    private bool _noBook;
    private bool _stale;
    private bool _approximate;
    private bool _loading = true;
    private string _error = string.Empty;
    private string _bestBid = Formatting.None;
    private string _bestAsk = Formatting.None;
    private string _spread = Formatting.None;
    private string _middle = Formatting.None;
    private string _asOf = string.Empty;
    private bool _disposed;

    /// <param name="preferences">The depth the book starts with, and where the depth chosen is remembered for the next one; without it the book starts with its own.</param>
    public OrderBookViewModel(string ticker, IKalTermApi api, MarketFeed feed, Formatting format, ViewPreferences? preferences = null)
    {
        _preferences = preferences;
        if (preferences?.BookDepth is { } depth && Depths.Contains(depth))
        {
            _depth = depth;
        }

        ArgumentNullException.ThrowIfNull(ticker);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(format);
        _ticker = ticker;
        _api = api;
        _feed = feed;
        _format = format;
        _format.ZoneChanged += Redraw;
    }

    public string Title { get; } = "Book";

    /// <summary>The asks, the farthest first, so the best ask is the last row.</summary>
    public ObservableCollection<BookRow> Asks { get; } = [];

    /// <summary>The bids, the best first.</summary>
    public ObservableCollection<BookRow> Bids { get; } = [];

    /// <summary>How many levels each side shows (the stream carries <see cref="MarketFeed.BookDepth"/>).</summary>
    public int Depth
    {
        get => _depth;
        set
        {
            if (Set(ref _depth, value))
            {
                _preferences?.SetBookDepth(value);
                Redraw();
            }
        }
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>The server publishes no book for this market (only the watched markets have one).</summary>
    public bool NoBook
    {
        get => _noBook;
        private set
        {
            if (Set(ref _noBook, value))
            {
                Raise(nameof(HasBook));
            }
        }
    }

    public bool HasBook => !_noBook && _last is not null;

    /// <summary>Messages were missed (or there is no book yet) and recovery has not finished: the levels cannot be trusted.</summary>
    public bool Stale
    {
        get => _stale;
        private set
        {
            if (Set(ref _stale, value))
            {
                Raise(nameof(Warning));
                Raise(nameof(HasWarning));
            }
        }
    }

    /// <summary>The book was rebuilt from a REST snapshot, which cannot be lined up with the live stream.</summary>
    public bool Approximate
    {
        get => _approximate;
        private set
        {
            if (Set(ref _approximate, value))
            {
                Raise(nameof(Warning));
                Raise(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => _stale || _approximate;

    public string Warning => _stale
        ? "This book is stale: updates were missed and recovery has not finished. Do not rely on it."
        : _approximate ? "This book was rebuilt from a snapshot and may not match the live market exactly." : string.Empty;

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

    public string BestBid
    {
        get => _bestBid;
        private set => Set(ref _bestBid, value);
    }

    public string BestAsk
    {
        get => _bestAsk;
        private set => Set(ref _bestAsk, value);
    }

    /// <summary>The best ask minus the best bid.</summary>
    public string Spread
    {
        get => _spread;
        private set => Set(ref _spread, value);
    }

    /// <summary>The middle of the best bid and the best ask.</summary>
    public string Middle
    {
        get => _middle;
        private set => Set(ref _middle, value);
    }

    /// <summary>When the book last changed, in the chosen zone.</summary>
    public string AsOf
    {
        get => _asOf;
        private set => Set(ref _asOf, value);
    }

    /// <summary>Asks the feed for the book and reads it once over REST so there is something to show before the first push.</summary>
    /// <returns>A task that completes when the first book is shown or the reason there is none is.</returns>
    public async Task LoadAsync()
    {
        _feed.Book += OnBook;
        _feed.SetWantedBooks(FeedOwner, [_ticker]);
        try
        {
            var book = await _api.GetOrderBookAsync(_ticker, MarketFeed.BookDepth).ConfigureAwait(true);
            if (_last is null)
            {
                Show(book);
            }
        }
        catch (ApiException ex) when (ex.Code == "not_found")
        {
            NoBook = true;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _format.ZoneChanged -= Redraw;
        _feed.Book -= OnBook;
        _feed.SetWantedBooks(FeedOwner, []);
    }

    private void OnBook(OrderBookDto book)
    {
        if (string.Equals(book.Ticker, _ticker, StringComparison.Ordinal))
        {
            NoBook = false;
            Error = string.Empty;
            Show(book);
        }
    }

    private void Show(OrderBookDto book)
    {
        _last = book;
        Stale = book.Stale;
        Approximate = book.Approximate;
        Raise(nameof(HasBook));
        Redraw();
    }

    /// <summary>Builds the ladder from the book last received, at the depth chosen.</summary>
    private void Redraw()
    {
        if (_last is not { } book)
        {
            return;
        }

        var bids = book.YesBids.Take(_depth).ToList();
        var asks = book.NoBids.Take(_depth).Select(no => new BookLevelDto(Formatting.Difference("1.000000", no.Price) ?? Formatting.None, no.Quantity)).ToList();
        var bidTotals = Totals(bids);
        var askTotals = Totals(asks);
        var most = Math.Max(Last(bidTotals), Last(askTotals));

        Asks.Clear();
        foreach (var i in Enumerable.Range(0, asks.Count).Reverse())
        {
            Asks.Add(Row(BookSide.Ask, asks[i], askTotals[i], most));
        }

        Bids.Clear();
        for (var i = 0; i < bids.Count; i++)
        {
            Bids.Add(Row(BookSide.Bid, bids[i], bidTotals[i], most));
        }

        BestBid = bids.Count > 0 ? Formatting.Dollars(bids[0].Price) : Formatting.None;
        BestAsk = asks.Count > 0 ? Formatting.Dollars(asks[0].Price) : Formatting.None;
        Spread = bids.Count > 0 && asks.Count > 0 && Formatting.Difference(asks[0].Price, bids[0].Price) is { } spread ? Formatting.Dollars(spread) : Formatting.None;
        Middle = bids.Count > 0 && asks.Count > 0 && Formatting.Midpoint(asks[0].Price, bids[0].Price) is { } middle ? Formatting.Dollars(middle) : Formatting.None;
        AsOf = _format.MomentWithZone(book.UpdatedAt);
    }

    private static BookRow Row(BookSide side, BookLevelDto level, long totalHundredths, long mostHundredths) =>
        new(side, Formatting.Dollars(level.Price), Formatting.Count(level.Quantity), Formatting.Count(Show(totalHundredths)), mostHundredths > 0 ? (double)totalHundredths / mostHundredths : 0);

    private static List<long> Totals(List<BookLevelDto> levels)
    {
        var running = 0L;
        var totals = new List<long>(levels.Count);
        foreach (var level in levels)
        {
            running += Formatting.Hundredths(level.Quantity) ?? 0;
            totals.Add(running);
        }

        return totals;
    }

    private static long Last(List<long> totals) => totals.Count > 0 ? totals[^1] : 0;

    private static string Show(long hundredths) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hundredths / 100}.{hundredths % 100:D2}");
}
