using KalTerm.Contracts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>
/// One row of the Markets grid. It starts from the screener's figures; a live tick updates the last price, the best bid and ask, the spread (worked out from the exact values) and the open interest. The 24-hour change and
/// volume stay as of the analyzer's last run: a tick carries total volume, not the last 24 hours, and a number that mixes the two would be wrong.
/// </summary>
internal sealed class MarketRowViewModel : ObservableObject
{
    private string _lists = string.Empty;
    private string? _bidText;
    private string? _askText;
    private string _last;
    private string _bid;
    private string _ask;
    private string _spread;
    private string _openInterest;

    public MarketRowViewModel(ScreenerRowDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Ticker = row.Ticker;
        EventTicker = row.EventTicker;
        Status = row.Status;
        YesLabel = row.YesLabel;
        Name = MarketNames.Name(row.EventTitle, row.Title, row.YesLabel, row.Ticker);
        Under = MarketNames.Under(row.EventTitle, row.Title);
        Change = Formatting.Signed(row.Change24h);
        Volume = Formatting.Count(row.Volume24h);
        _bidText = row.YesBid;
        _askText = row.YesAsk;
        _last = Formatting.Dollars(row.Last);
        _bid = Formatting.Dollars(row.YesBid);
        _ask = Formatting.Dollars(row.YesAsk);
        _spread = Formatting.Dollars(row.Spread);
        _openInterest = Formatting.Count(row.OpenInterest);
    }

    public string Ticker { get; }

    /// <summary>The watchlists the market is in, by name, comma separated; empty when it is in none.</summary>
    public string Lists
    {
        get => _lists;
        private set => Set(ref _lists, value);
    }

    /// <summary>Tells the row which watchlists it is in.</summary>
    /// <param name="names">The names of the lists.</param>
    public void SetLists(IReadOnlyList<string> names) => Lists = string.Join(", ", names);

    public string EventTicker { get; }

    public string Status { get; }

    public string YesLabel { get; }

    /// <summary>What the market is called: its event's title, else its own, else the yes label, else the ticker.</summary>
    public string Name { get; }

    /// <summary>Under the name: the market's own title when the name is the event's; empty otherwise.</summary>
    public string Under { get; }

    /// <summary>Whether there is a line to show under the name.</summary>
    public bool HasUnder => Under.Length > 0;

    /// <summary>As of the analyzer's last run.</summary>
    public string Change { get; }

    /// <summary>Contracts traded in 24 hours, as of the analyzer's last run.</summary>
    public string Volume { get; }

    public string Last
    {
        get => _last;
        private set => Set(ref _last, value);
    }

    public string Bid
    {
        get => _bid;
        private set => Set(ref _bid, value);
    }

    public string Ask
    {
        get => _ask;
        private set => Set(ref _ask, value);
    }

    public string Spread
    {
        get => _spread;
        private set => Set(ref _spread, value);
    }

    public string OpenInterest
    {
        get => _openInterest;
        private set => Set(ref _openInterest, value);
    }

    /// <summary>Applies a live ticker update. A field the update does not carry keeps what it had: an absent field is not an empty book.</summary>
    /// <param name="tick">The update.</param>
    public void Apply(TickDto tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (tick.Price is not null)
        {
            Last = Formatting.Dollars(tick.Price);
        }

        if (tick.YesBid is not null)
        {
            _bidText = tick.YesBid;
            Bid = Formatting.Dollars(tick.YesBid);
        }

        if (tick.YesAsk is not null)
        {
            _askText = tick.YesAsk;
            Ask = Formatting.Dollars(tick.YesAsk);
        }

        if (Formatting.Difference(_askText, _bidText) is { } spread)
        {
            Spread = Formatting.Dollars(spread);
        }

        if (tick.OpenInterest is not null)
        {
            OpenInterest = Formatting.Count(tick.OpenInterest);
        }
    }
}
