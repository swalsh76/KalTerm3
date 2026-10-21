using System.Globalization;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>A candle length to choose: what the person reads, what the API is asked for, and how many seconds that is.</summary>
internal sealed record IntervalChoice(string Text, string Value, int Seconds);

/// <summary>How much history to show: what the person reads and how long it is (<see langword="null"/> for as many candles as the chart holds).</summary>
internal sealed record RangeChoice(string Text, TimeSpan? Span);

/// <summary>
/// The candle chart of one market: the candles of the interval and range chosen, loaded as the latest ones (so what you see ends now), with the newest candle asked for again every <see cref="RefreshEvery"/>
/// (a candle that is still forming changes as trades come in) and the last traded price kept live from the stream. Choosing another interval or range loads again; an answer that arrives after a newer choice is thrown away.
/// What is drawn is made from the candles by <see cref="CandlePlotBuilder"/>; this class owns what to draw and says when it changed (<see cref="SeriesChanged"/>).
/// </summary>
internal sealed class CandleChartViewModel : ObservableObject, IDisposable
{
    /// <summary>The most candles one chart holds.</summary>
    public const int MaxCandles = 1_000;

    /// <summary>How often the newest candles are asked for again.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    /// <summary>How many of the newest candles a refresh asks for (the one forming, and the one before it in case it closed).</summary>
    private const int RefreshCandles = 3;

    private readonly string _ticker;
    private readonly IKalTermApi _api;
    private readonly MarketFeed _feed;
    private readonly Formatting _format;
    private readonly TimeProvider _time;
    private readonly List<ChartCandle> _candles = [];
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _loop;
    private IntervalChoice _interval;
    private RangeChoice _range;
    private ChartSeries _series = ChartSeries.Empty;
    private string? _lastPrice;
    private string _hoverText = string.Empty;
    private string _source = string.Empty;
    private string _error = string.Empty;
    private bool _loading = true;
    private bool _disposed;

    public CandleChartViewModel(string ticker, IKalTermApi api, MarketFeed feed, Formatting format, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(time);
        _ticker = ticker;
        _api = api;
        _feed = feed;
        _format = format;
        _time = time;
        Intervals =
        [
            new IntervalChoice("1m", "1m", 60),
            new IntervalChoice("5m", "5m", 300),
            new IntervalChoice("15m", "15m", 900),
            new IntervalChoice("1h", "1h", 3600),
            new IntervalChoice("4h", "4h", 14400),
            new IntervalChoice("1d", "1d", 86400),
        ];
        Ranges =
        [
            new RangeChoice("1 day", TimeSpan.FromDays(1)),
            new RangeChoice("1 week", TimeSpan.FromDays(7)),
            new RangeChoice("1 month", TimeSpan.FromDays(30)),
            new RangeChoice("All", null),
        ];
        _interval = Intervals[3];
        _range = Ranges[1];
        _format.ZoneChanged += OnZoneChanged;
    }

    public string Title { get; } = "Chart";

    public IReadOnlyList<IntervalChoice> Intervals { get; }

    public IReadOnlyList<RangeChoice> Ranges { get; }

    /// <summary>The drawing needs doing again: new candles, a new last price, or another time zone.</summary>
    public event Action? SeriesChanged;

    public IntervalChoice Interval
    {
        get => _interval;
        set
        {
            if (Set(ref _interval, value))
            {
                _ = SafeLoadAsync();
            }
        }
    }

    public RangeChoice Range
    {
        get => _range;
        set
        {
            if (Set(ref _range, value))
            {
                _ = SafeLoadAsync();
            }
        }
    }

    /// <summary>The candles to draw, oldest first.</summary>
    public ChartSeries Series
    {
        get => _series;
        private set
        {
            if (Set(ref _series, value))
            {
                Raise(nameof(HasCandles));
                Raise(nameof(NothingToShow));
            }
        }
    }

    /// <summary>The zone the time axis is drawn in.</summary>
    public TimeZoneInfo Zone => _format.Zone;

    /// <summary>The last traded price as text (a dollar amount), or <see langword="null"/> before there is one.</summary>
    public string? LastPrice => _lastPrice;

    public string LastPriceText => _lastPrice is null ? Formatting.None : Formatting.Dollars(_lastPrice);

    public bool HasCandles => _series.Candles.Count > 0;

    /// <summary>The last ask worked and there was nothing to draw: no trades in this range.</summary>
    public bool NothingToShow => !_loading && _error.Length == 0 && _series.Candles.Count == 0;

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (Set(ref _loading, value))
            {
                Raise(nameof(NothingToShow));
            }
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(NothingToShow));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>How the server got the candles (stored, rolled up, or computed from raw rows).</summary>
    public string Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    /// <summary>The candle under the pointer, in words and exact figures; empty when the pointer is not over one.</summary>
    public string HoverText
    {
        get => _hoverText;
        private set => Set(ref _hoverText, value);
    }

    /// <summary>Loads the chart, starts the refresh timer and follows the last price on the stream.</summary>
    /// <returns>A task that completes when the first load is done (or has failed, which <see cref="Error"/> says).</returns>
    public async Task LoadAsync()
    {
        _feed.Tick += OnTick;
        _feed.SetWanted("chart", [_ticker]);
        _loop = new CancellationTokenSource();
        await FetchAsync().ConfigureAwait(true);
        if (!_disposed)
        {
            _ = RefreshLoopAsync(_loop.Token);
        }
    }

    /// <summary>Says what is under the pointer.</summary>
    /// <param name="moment">The time under the pointer as drawn (in <see cref="Zone"/>), or <see langword="null"/> when the pointer is off the chart.</param>
    public void HoverAt(DateTime? moment)
    {
        if (moment is not { } local)
        {
            HoverText = string.Empty;
            return;
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _format.Zone);
        HoverText = _series.At(utc) is { } candle ? Describe(candle.Source) : string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _format.ZoneChanged -= OnZoneChanged;
        _feed.Tick -= OnTick;
        _feed.SetWanted("chart", []);
        _load?.Cancel();
        _load?.Dispose();
        _loop?.Cancel();
        _loop?.Dispose();
    }

    private string Describe(CandleDto candle)
    {
        var parts = new List<string>
        {
            _format.MomentWithZone(candle.Time),
            candle.Open is null ? "no trades" : $"open {Formatting.Dollars(candle.Open)}  high {Formatting.Dollars(candle.High)}  low {Formatting.Dollars(candle.Low)}  close {Formatting.Dollars(candle.Close)}",
            $"volume {Formatting.Count(candle.Volume)}",
            $"trades {candle.TradeCount.ToString("N0", CultureInfo.InvariantCulture)}",
        };
        if (candle.YesBid is not null || candle.YesAsk is not null)
        {
            parts.Add($"bid {Formatting.Dollars(candle.YesBid)}  ask {Formatting.Dollars(candle.YesAsk)}");
        }

        if (candle.OpenInterest is not null)
        {
            parts.Add($"open interest {Formatting.Count(candle.OpenInterest)}");
        }

        if (candle.Partial)
        {
            parts.Add(candle.GapKind is { Length: > 0 } kind ? $"data may be missing ({kind.Replace('_', ' ')}, {candle.GapSeconds ?? 0} s)" : "data may be missing");
        }

        return string.Join("   ·   ", parts);
    }

    private int CandlesWanted() => _range.Span is { } span ? (int)Math.Clamp(Math.Ceiling(span.TotalSeconds / _interval.Seconds), 1, MaxCandles) : MaxCandles;

    private async Task SafeLoadAsync()
    {
        try
        {
            await FetchAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task FetchAsync()
    {
        _load?.Cancel();
        _load?.Dispose();
        var source = new CancellationTokenSource();
        _load = source;
        var token = source.Token;
        var interval = _interval;
        Loading = true;
        try
        {
            var page = await _api.GetCandlesAsync(_ticker, interval.Value, new WindowQuery(Limit: CandlesWanted()), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _candles.Clear();
            _candles.AddRange(page.Candles.Select(ChartCandle.From));
            Source = page.Source;
            Error = string.Empty;
            Publish(interval);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
        {
            if (!token.IsCancellationRequested)
            {
                Error = ex is ApiException { Code: "not_found" } ? "no such market" : ex.Message;
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

    /// <summary>Asks for the newest candles again and folds them into what is held: a candle with a start already held replaces it (it was still forming), a newer one is added, and the oldest drop off past the limit.</summary>
    private async Task RefreshAsync()
    {
        var interval = _interval;
        var page = await _api.GetCandlesAsync(_ticker, interval.Value, new WindowQuery(Limit: RefreshCandles), _loop?.Token ?? default).ConfigureAwait(true);
        if (interval != _interval || _candles.Count == 0 && page.Candles.Count == 0)
        {
            return;   // another interval was chosen meanwhile: its own load decides what is shown
        }

        foreach (var fresh in page.Candles.Select(ChartCandle.From))
        {
            var at = _candles.FindIndex(c => c.Start == fresh.Start);
            if (at >= 0)
            {
                _candles[at] = fresh;
            }
            else if (_candles.Count == 0 || fresh.Start > _candles[^1].Start)
            {
                _candles.Add(fresh);
            }
        }

        while (_candles.Count > MaxCandles)
        {
            _candles.RemoveAt(0);
        }

        Error = string.Empty;
        Publish(interval);
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(RefreshEvery, _time, token).ConfigureAwait(true);
                try
                {
                    await RefreshAsync().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is ApiException or ApiUnavailableException or TlsTrustException)
                {
                    Error = ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
    }

    private void Publish(IntervalChoice interval)
    {
        Series = new ChartSeries(interval.Seconds, [.. _candles]);
        if (_lastPrice is null && _candles.LastOrDefault(c => c.HasPrice)?.Source.Close is { } close)
        {
            _lastPrice = close;   // until the stream says otherwise, the newest trade the candles know of
            Raise(nameof(LastPrice));
            Raise(nameof(LastPriceText));
        }

        SeriesChanged?.Invoke();
    }

    private void OnTick(MarketTickDto tick)
    {
        if (string.Equals(tick.Ticker, _ticker, StringComparison.Ordinal) && tick.Tick.Price is { } price && price != _lastPrice)
        {
            _lastPrice = price;
            Raise(nameof(LastPrice));
            Raise(nameof(LastPriceText));
            SeriesChanged?.Invoke();
        }
    }

    private void OnZoneChanged()
    {
        Raise(nameof(Zone));
        SeriesChanged?.Invoke();
    }
}
