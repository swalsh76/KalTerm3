using System.Collections.ObjectModel;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>One labelled line of the detail.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Text">Its value.</param>
internal sealed record DetailLine(string Label, string Text);

/// <summary>One ticker update as shown. <paramref name="Source"/> is what the server sent, kept so the times can be redrawn in another zone and a tick that came twice can be told.</summary>
internal sealed record TickRow(string Time, string Price, string Bid, string Ask, string BidSize, string AskSize, string Volume, string OpenInterest, TickDto Source);

/// <summary>One trade as shown.</summary>
internal sealed record TradeRow(string Time, string YesPrice, string Count, string Side, string Kind, TradeDto Source);

/// <summary>
/// One market opened over the list: what it is, its metrics, its recent ticks with new ones arriving live, and its recent trades. The ticks come from the REST history and then from the feed (the feed is told first, so
/// nothing falls between the two; a tick that arrives from both is shown once). The stream carries ticks and books, not trades, so the trades and the metrics are asked for again on a timer.
/// </summary>
internal sealed class MarketDetailViewModel : ObservableObject, IDisposable
{
    /// <summary>Ticks asked for when the market opens.</summary>
    public const int HistoryTicks = 200;

    /// <summary>The most ticks kept; older ones drop off.</summary>
    public const int MaxTicks = 500;

    /// <summary>Trades kept.</summary>
    public const int HistoryTrades = 100;

    /// <summary>How often the metrics are asked for again (the analyzer runs about every minute).</summary>
    public static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(60);

    /// <summary>How often the trades are asked for again.</summary>
    public static readonly TimeSpan TradesInterval = TimeSpan.FromSeconds(5);

    private const string FeedOwner = "detail";
    private const int RecentToCompare = 50;

    private readonly IKalTermApi _api;
    private readonly MarketFeed _feed;
    private readonly Formatting _format;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loops;
    private MarketDto? _market;
    private MetricsDto? _metrics;
    private string _heading;
    private string _subHeading = string.Empty;
    private int _selectedTab;
    private CandleChartViewModel? _chart;
    private OrderBookViewModel? _book;
    private bool _loading = true;
    private bool _disposed;

    public MarketDetailViewModel(string ticker, IKalTermApi api, MarketFeed feed, Formatting format, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(time);
        Ticker = ticker;
        _heading = ticker;
        _api = api;
        _feed = feed;
        _format = format;
        _time = time;
        BackCommand = new RelayCommand(() => Closed?.Invoke());
        _format.ZoneChanged += Redraw;
    }

    /// <summary>The person asked to go back to the list.</summary>
    public event Action? Closed;

    public string Ticker { get; }

    public RelayCommand BackCommand { get; }

    /// <summary>What the market is called (its event's title), the ticker until it is known.</summary>
    public string Heading
    {
        get => _heading;
        private set => Set(ref _heading, value);
    }

    /// <summary>Under the heading: the market's own title and its ticker.</summary>
    public string SubHeading
    {
        get => _subHeading;
        private set => Set(ref _subHeading, value);
    }

    /// <summary>The tab shown: 0 the overview, 1 the chart, 2 the book. The chart and the book are made, and begin loading, the first time their tab is chosen.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value))
            {
                if (value == 1 && _chart is null)
                {
                    Chart = new CandleChartViewModel(Ticker, _api, _feed, _format, _time);
                    _ = StartAsync(Chart.LoadAsync);
                }
                else if (value == 2 && _book is null)
                {
                    Book = new OrderBookViewModel(Ticker, _api, _feed, _format);
                    _ = StartAsync(Book.LoadAsync);
                }
            }
        }
    }

    /// <summary>The candle chart, or <see langword="null"/> until its tab is first chosen.</summary>
    public CandleChartViewModel? Chart
    {
        get => _chart;
        private set => Set(ref _chart, value);
    }

    /// <summary>The order book, or <see langword="null"/> until its tab is first chosen.</summary>
    public OrderBookViewModel? Book
    {
        get => _book;
        private set => Set(ref _book, value);
    }

    public ObservableCollection<DetailLine> About { get; } = [];

    public ObservableCollection<DetailLine> Metrics { get; } = [];

    /// <summary>Newest first.</summary>
    public ObservableCollection<TickRow> Ticks { get; } = [];

    /// <summary>Newest first.</summary>
    public ObservableCollection<TradeRow> Trades { get; } = [];

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>What could not be loaded or refreshed, one sentence for each; empty when all is well.</summary>
    public string Error => string.Join("  ", _errors.Values);

    public bool HasError => _errors.Count > 0;

    /// <summary>Tells the feed this market is wanted, then loads everything and starts the refresh timers.</summary>
    /// <returns>A task that completes when the first load is done (each part that failed is reported in <see cref="Error"/>).</returns>
    public async Task LoadAsync()
    {
        _feed.Tick += OnTick;
        _feed.SetWanted(FeedOwner, [Ticker]);
        _loops = new CancellationTokenSource();
        await Task.WhenAll(Section("market", LoadMarketAsync), Section("metrics", LoadMetricsAsync), Section("ticks", LoadTicksAsync), Section("trades", LoadTradesAsync)).ConfigureAwait(true);
        Loading = false;
        if (!_disposed)
        {
            _ = LoopAsync(MetricsInterval, "metrics", LoadMetricsAsync, _loops.Token);
            _ = LoopAsync(TradesInterval, "trades", LoadTradesAsync, _loops.Token);
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
        _feed.Tick -= OnTick;
        _chart?.Dispose();
        _book?.Dispose();
        _feed.SetWanted(FeedOwner, []);
        _loops?.Cancel();
        _loops?.Dispose();
        _loops = null;
    }

    /// <summary>Runs the first load of a tab. A failure is reported by the tab itself; anything unexpected is not allowed to vanish with the discarded task.</summary>
    private static async Task StartAsync(Func<Task> load)
    {
        try
        {
            await load().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            // the tab shows its own error
        }
    }

    private async Task Section(string name, Func<Task> load)
    {
        try
        {
            await load().ConfigureAwait(true);
            Report(name, null);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            Report(name, $"{name}: {(ex is ApiException { Code: "not_found" } ? "no such market" : ex.Message)}");
        }
    }

    private void Report(string name, string? message)
    {
        var changed = message is null ? _errors.Remove(name) : !_errors.TryGetValue(name, out var before) || before != message;
        if (message is not null)
        {
            _errors[name] = message;
        }

        if (changed)
        {
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    private async Task LoopAsync(TimeSpan interval, string name, Func<Task> load, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, token).ConfigureAwait(true);
                await Section(name, load).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
    }

    private async Task LoadMarketAsync()
    {
        _market = await _api.GetMarketAsync(Ticker, _loops?.Token ?? default).ConfigureAwait(true);
        Heading = MarketNames.Name(_market.EventTitle, _market.Title, _market.YesLabel, Ticker);
        SubHeading = string.Join("  ·  ", new[] { MarketNames.Under(_market.EventTitle, _market.Title), Ticker }.Where(part => part.Length > 0 && !string.Equals(part, Heading, StringComparison.Ordinal)));
        RedrawLines();
    }

    private async Task LoadMetricsAsync()
    {
        _metrics = await _api.GetMetricsAsync(Ticker, _loops?.Token ?? default).ConfigureAwait(true);
        RedrawLines();
    }

    private async Task LoadTicksAsync()
    {
        var page = await _api.GetTicksAsync(Ticker, new WindowQuery(Limit: HistoryTicks), _loops?.Token ?? default).ConfigureAwait(true);
        // what the stream delivered while the history was loading is already in the list; the history goes under it, newest first, and a tick known twice is shown once
        var known = Ticks.Select(t => t.Source).ToHashSet();
        var older = page.Ticks.Reverse().Where(t => !known.Contains(t)).Select(ToRow).ToList();
        var merged = Ticks.Concat(older).OrderByDescending(t => t.Source.Time, StringComparer.Ordinal).Take(MaxTicks).ToList();
        Ticks.Clear();
        foreach (var row in merged)
        {
            Ticks.Add(row);
        }
    }

    private async Task LoadTradesAsync()
    {
        var page = await _api.GetTradesAsync(Ticker, new WindowQuery(Limit: HistoryTrades), _loops?.Token ?? default).ConfigureAwait(true);
        var rows = page.Trades.Reverse().Select(ToRow).ToList();
        if (rows.Select(r => r.Source).SequenceEqual(Trades.Select(r => r.Source)))
        {
            return;
        }

        Trades.Clear();
        foreach (var row in rows)
        {
            Trades.Add(row);
        }
    }

    private void OnTick(MarketTickDto tick)
    {
        if (!string.Equals(tick.Ticker, Ticker, StringComparison.Ordinal) || Ticks.Take(RecentToCompare).Any(t => t.Source == tick.Tick))
        {
            return;
        }

        Ticks.Insert(0, ToRow(tick.Tick));
        while (Ticks.Count > MaxTicks)
        {
            Ticks.RemoveAt(Ticks.Count - 1);
        }
    }

    private TickRow ToRow(TickDto tick) => new(
        _format.Moment(tick.Time, milliseconds: true),
        Formatting.Dollars(tick.Price),
        Formatting.Dollars(tick.YesBid),
        Formatting.Dollars(tick.YesAsk),
        Formatting.Count(tick.YesBidSize),
        Formatting.Count(tick.YesAskSize),
        Formatting.Count(tick.Volume),
        Formatting.Count(tick.OpenInterest),
        tick);

    private TradeRow ToRow(TradeDto trade) => new(
        _format.Moment(trade.Time, milliseconds: true),
        Formatting.Dollars(trade.YesPrice),
        Formatting.Count(trade.Count),
        trade.TakerSide ?? Formatting.None,
        trade.IsBlockTrade ? "block" : string.Empty,
        trade);

    /// <summary>Builds every shown time again, in the zone now chosen.</summary>
    private void Redraw()
    {
        RedrawLines();
        var ticks = Ticks.Select(t => ToRow(t.Source)).ToList();
        Ticks.Clear();
        foreach (var row in ticks)
        {
            Ticks.Add(row);
        }

        var trades = Trades.Select(t => ToRow(t.Source)).ToList();
        Trades.Clear();
        foreach (var row in trades)
        {
            Trades.Add(row);
        }
    }

    /// <summary>Builds the description and the metrics again: new figures, or another time zone.</summary>
    private void RedrawLines()
    {
        About.Clear();
        if (_market is { } market)
        {
            About.Add(new DetailLine("Event", market.EventTicker));
            if (market.EventSubTitle.Length > 0)
            {
                About.Add(new DetailLine("Event sub-title", market.EventSubTitle));
            }

            About.Add(new DetailLine("Status", market.Status));
            About.Add(new DetailLine("Yes / No", $"{market.YesLabel} / {market.NoLabel}"));
            About.Add(new DetailLine("Opens", _format.MomentWithZone(market.OpenTime)));
            About.Add(new DetailLine("Closes", _format.MomentWithZone(market.CloseTime)));
            About.Add(new DetailLine("Expires", _format.MomentWithZone(market.ExpirationTime)));
            if (market.Result.Length > 0)
            {
                About.Add(new DetailLine("Result", market.Result));
                About.Add(new DetailLine("Settled", $"{Formatting.Dollars(market.SettlementValue)} at {_format.MomentWithZone(market.SettlementTime)}"));
            }
        }

        Metrics.Clear();
        if (_metrics is { } m)
        {
            Metrics.Add(new DetailLine("Last", Formatting.Dollars(m.Last)));
            Metrics.Add(new DetailLine("Bid / Ask", $"{Formatting.Dollars(m.YesBid)} / {Formatting.Dollars(m.YesAsk)}"));
            Metrics.Add(new DetailLine("Spread", Formatting.Dollars(m.Spread)));
            Metrics.Add(new DetailLine("24-hour change", Formatting.Signed(m.Change24h)));
            Metrics.Add(new DetailLine("24-hour volume", Formatting.Count(m.Volume24h)));
            Metrics.Add(new DetailLine("24-hour trades", m.TradeCount24h.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
            Metrics.Add(new DetailLine("Open interest", Formatting.Count(m.OpenInterest)));
            Metrics.Add(new DetailLine("VWAP (24 hours)", Formatting.Dollars(m.Vwap24h)));
            Metrics.Add(new DetailLine("SMA 20 / 50 (hourly)", $"{Formatting.Dollars(m.Sma20)} / {Formatting.Dollars(m.Sma50)}"));
            Metrics.Add(new DetailLine("Volatility (24 hours)", Formatting.Dollars(m.Volatility24h)));
            Metrics.Add(new DetailLine("As of", _format.MomentWithZone(m.AsOf) + (m.Partial ? "  (a gap may have left a figure incomplete)" : string.Empty)));
        }
    }
}
