using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;

namespace KalTerm.Kalshi.Books;

/// <summary>
/// One market's order book, built from Kalshi's <c>orderbook_snapshot</c> and <c>orderbook_delta</c> messages. Kalshi sends bids only: a YES bid at X is a NO ask
/// at 1 - X, so the asks are derived. Prices are exact fixed-point <see cref="Dollars"/> and quantities exact <see cref="Contracts"/>; a level is removed when its
/// quantity reaches zero. Not thread-safe: one consumer drives it.
/// </summary>
public sealed class LocalOrderBook
{
    private readonly Dictionary<Dollars, Contracts> _yes;
    private readonly Dictionary<Dollars, Contracts> _no;

    /// <summary>Creates a book from bids on each side; levels with no quantity are dropped.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="yes">Yes bids.</param>
    /// <param name="no">No bids.</param>
    /// <param name="approximate"><see langword="true"/> if the book came from a REST snapshot, which carries no sequence number and so cannot be aligned with the delta stream.</param>
    public LocalOrderBook(string ticker, IEnumerable<PriceLevel>? yes = null, IEnumerable<PriceLevel>? no = null, bool approximate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        Ticker = ticker;
        Approximate = approximate;
        _yes = Levels(yes);
        _no = Levels(no);
    }

    private LocalOrderBook(string ticker, Dictionary<Dollars, Contracts> yes, Dictionary<Dollars, Contracts> no, bool approximate)
    {
        Ticker = ticker;
        _yes = yes;
        _no = no;
        Approximate = approximate;
    }

    /// <summary>The market.</summary>
    public string Ticker { get; }

    /// <summary>True if built from a REST snapshot (not aligned with the delta stream).</summary>
    public bool Approximate { get; }

    /// <summary>Number of yes bid levels.</summary>
    public int YesLevels => _yes.Count;

    /// <summary>Number of no bid levels.</summary>
    public int NoLevels => _no.Count;

    /// <summary>The highest yes bid.</summary>
    public Dollars? BestYesBid => _yes.Count == 0 ? null : _yes.Keys.Max();

    /// <summary>The highest no bid.</summary>
    public Dollars? BestNoBid => _no.Count == 0 ? null : _no.Keys.Max();

    /// <summary>The lowest yes ask: one dollar minus the best no bid.</summary>
    public Dollars? BestYesAsk => BestNoBid is { } bid ? Dollars.FromMicros(1_000_000 - bid.Micros) : null;

    /// <summary>The lowest no ask: one dollar minus the best yes bid.</summary>
    public Dollars? BestNoAsk => BestYesBid is { } bid ? Dollars.FromMicros(1_000_000 - bid.Micros) : null;

    /// <summary>Builds a book from a snapshot message.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The book.</returns>
    public static LocalOrderBook FromSnapshot(OrderbookSnapshotMsg snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new LocalOrderBook(snapshot.MarketTicker, snapshot.YesDollarsFp, snapshot.NoDollarsFp);
    }

    /// <summary>Builds an approximate book from a REST response.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="book">The REST order book.</param>
    /// <returns>The book, marked <see cref="Approximate"/>.</returns>
    public static LocalOrderBook FromRest(string ticker, Models.OrderBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        return new LocalOrderBook(ticker, book.Yes, book.No, approximate: true);
    }

    /// <summary>
    /// Applies a signed change. As in the reference, any side other than <c>yes</c> is the no side. Returns <see langword="false"/> and changes nothing
    /// if the quantity at that price would go negative (or overflow).
    /// </summary>
    /// <param name="delta">The change.</param>
    /// <returns>Whether it was applied.</returns>
    public bool ApplyDelta(OrderbookDeltaMsg delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var levels = delta.Side == "yes" ? _yes : _no;
        levels.TryGetValue(delta.PriceDollars, out var current);
        long quantity;
        try
        {
            quantity = checked(current.Hundredths + delta.DeltaFp.Hundredths);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (quantity < 0)
        {
            return false;
        }

        if (quantity == 0)
        {
            levels.Remove(delta.PriceDollars);
        }
        else
        {
            levels[delta.PriceDollars] = Contracts.FromHundredths(quantity);
        }

        return true;
    }

    /// <summary>The bids of one side, best (highest) price first.</summary>
    /// <param name="side"><c>yes</c> or <c>no</c>.</param>
    /// <returns>The levels.</returns>
    public IReadOnlyList<PriceLevel> Bids(string side) =>
        [.. (side == "yes" ? _yes : _no).OrderByDescending(p => p.Key).Select(p => new PriceLevel(p.Key, p.Value))];

    /// <summary>An independent copy, so a consumer can keep a book as it was at one moment.</summary>
    /// <returns>The copy.</returns>
    public LocalOrderBook Copy() => new(Ticker, new Dictionary<Dollars, Contracts>(_yes), new Dictionary<Dollars, Contracts>(_no), Approximate);

    private static Dictionary<Dollars, Contracts> Levels(IEnumerable<PriceLevel>? levels)
    {
        var result = new Dictionary<Dollars, Contracts>();
        foreach (var level in levels ?? [])
        {
            if (level.Quantity.Hundredths > 0)
            {
                result[level.Price] = level.Quantity;
            }
        }

        return result;
    }
}
