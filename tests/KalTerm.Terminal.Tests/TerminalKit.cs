using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Security;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Tests;

/// <summary>Runs code on a thread of its own with a single-threaded apartment, the way WPF needs; whatever it throws comes back to the test.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure is null ? result : throw new InvalidOperationException("the STA work failed: " + failure.Message, failure);
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });
}

/// <summary>Collects WPF's binding complaints (a wrong property name is otherwise silent), so a test can insist there are none.</summary>
internal sealed class BindingErrors : TraceListener
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public static BindingErrors Listen()
    {
        var listener = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        return listener;
    }

    public override void Write(string? message) => _messages.Add(message ?? string.Empty);

    public override void WriteLine(string? message) => _messages.Add(message ?? string.Empty);

    protected override void Dispose(bool disposing)
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}

internal static class VisualTreeKit
{
    /// <summary>Forces templates, bindings and layout to happen without showing the element.</summary>
    public static void Realize(FrameworkElement element)
    {
        var target = Root(element);
        target.Measure(new Size(1000, 800));
        target.Arrange(new Rect(0, 0, 1000, 800));
        target.UpdateLayout();
    }

    /// <summary>A window that is not shown has no visual tree of its own; its content is what gets laid out.</summary>
    private static FrameworkElement Root(FrameworkElement element) => element is Window { Content: FrameworkElement content } ? content : element;

    public static IEnumerable<T> All<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is Window { Content: DependencyObject content })
        {
            root = content;
            if (content is T self)
            {
                yield return self;
            }
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in All<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    public static IEnumerable<string> Texts(DependencyObject root) => All<TextBlock>(root).Where(Shown).Select(t => t.Text);
}

/// <summary>An API that answers with what a test gave it. Only the calls the terminal makes so far are real; the rest refuse.</summary>
internal sealed class FakeApi : IKalTermApi, IDisposable
{
    public StatusDto Status { get; set; } = Samples.Status();

    public Exception? StatusFails { get; set; }

    public Exception? MarketsFail { get; set; }

    public string? StoredZone { get; set; }

    public Exception? ZoneFails { get; set; }

    public List<string?> ZonesStored { get; } = [];

    public int StatusCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusCalls++;
        return StatusFails is null ? Task.FromResult(Status) : Task.FromException<StatusDto>(StatusFails);
    }

    public Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default) =>
        MarketsFail is null ? Task.FromResult(new MarketPage([], null)) : Task.FromException<MarketPage>(MarketsFail);

    public Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default) =>
        ZoneFails is null ? Task.FromResult(new SettingDto("time_zone", StoredZone)) : Task.FromException<SettingDto>(ZoneFails);

    public Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default)
    {
        ZonesStored.Add(zone);
        return ZoneFails is null ? Task.CompletedTask : Task.FromException(ZoneFails);
    }

    public void Dispose() => Disposed = true;

    public MarketDto Market { get; set; } = Samples.Market("KX-A");

    public Exception? MarketFails { get; set; }

    public Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default) =>
        MarketFails is null ? Task.FromResult(Market with { Ticker = ticker }) : Task.FromException<MarketDto>(MarketFails);

    /// <summary>What the screener answers; by default one page with the rows in <see cref="ScreenerRows"/>.</summary>
    public Func<ScreenQuery, ScreenerPage>? ScreenerAnswer { get; set; }

    public List<ScreenerRowDto> ScreenerRows { get; } = [];

    public Exception? ScreenerFails { get; set; }

    /// <summary>When set, the screener does not answer until the test completes this.</summary>
    public TaskCompletionSource<ScreenerPage>? ScreenerHold { get; set; }

    public List<ScreenQuery> ScreenQueries { get; } = [];

    public async Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default)
    {
        ScreenQueries.Add(query);
        if (ScreenerFails is not null)
        {
            throw ScreenerFails;
        }

        if (ScreenerHold is { } hold)
        {
            return await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return ScreenerAnswer is { } answer ? answer(query) : new ScreenerPage("volume", "desc", "2026-10-18T22:58:00.000000+00:00", 120, [.. ScreenerRows], null);
    }

    public Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>What the market stream says: a test writes events here and the open stream yields them.</summary>
    public Channel<MarketStreamEvent> Stream { get; } = Channel.CreateUnbounded<MarketStreamEvent>();

    public List<MarketStreamRequest> StreamOpens { get; } = [];

    /// <summary>The live changes asked for, as <c>add|remove ticks|books TICKER</c>.</summary>
    public List<string> StreamChanges { get; } = [];

    public Exception? StreamChangeFails { get; set; }

    public Exception? StreamFails { get; set; }

    public async IAsyncEnumerable<MarketStreamEvent> StreamMarketsAsync(MarketStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamOpens.Add(request);
        if (StreamFails is not null)
        {
            throw StreamFails;
        }

        await foreach (var item in Stream.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task AddStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) => Change("add", kind, ticker);

    public Task RemoveStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) => Change("remove", kind, ticker);

    private Task Change(string verb, StreamKind kind, string ticker)
    {
        StreamChanges.Add($"{verb} {kind.ToString().ToLowerInvariant()} {ticker}");
        return StreamChangeFails is null ? Task.CompletedTask : Task.FromException(StreamChangeFails);
    }

    public MetricsDto Metrics { get; set; } = Samples.Metrics("KX-A");

    public Exception? MetricsFail { get; set; }

    public int MetricsCalls { get; private set; }

    public Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default)
    {
        MetricsCalls++;
        return MetricsFail is null ? Task.FromResult(Metrics with { Ticker = ticker }) : Task.FromException<MetricsDto>(MetricsFail);
    }

    /// <summary>Oldest first, as the server sends them.</summary>
    public List<TickDto> TickHistory { get; } = [];

    public Exception? TicksFail { get; set; }

    public List<WindowQuery> TickQueries { get; } = [];

    public Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        TickQueries.Add(query);
        return TicksFail is null ? Task.FromResult(new TickPage([.. TickHistory], null)) : Task.FromException<TickPage>(TicksFail);
    }

    /// <summary>What the candle endpoint answers: by default the candles in <see cref="CandleRows"/> (oldest first), the last <c>Limit</c> of them.</summary>
    public Func<string, WindowQuery, CandlePage>? CandleAnswer { get; set; }

    public List<CandleDto> CandleRows { get; } = [];

    public Exception? CandlesFail { get; set; }

    /// <summary>When set, the candles are not answered until the test completes this.</summary>
    public TaskCompletionSource<CandlePage>? CandleHold { get; set; }

    public List<(string Ticker, string Interval, WindowQuery Query)> CandleQueries { get; } = [];

    public async Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default)
    {
        CandleQueries.Add((ticker, interval, query));
        if (CandlesFail is not null)
        {
            throw CandlesFail;
        }

        if (CandleHold is { } hold)
        {
            return await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CandleAnswer is { } answer)
        {
            return answer(interval, query);
        }

        var limit = query.Limit ?? 200;
        return new CandlePage(interval, "stored " + interval, [.. CandleRows.Skip(Math.Max(0, CandleRows.Count - limit))], null);
    }

    /// <summary>Oldest first, as the server sends them.</summary>
    public List<TradeDto> TradeHistory { get; } = [];

    public Exception? TradesFail { get; set; }

    public int TradeCalls { get; private set; }

    public Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        TradeCalls++;
        return TradesFail is null ? Task.FromResult(new TradePage([.. TradeHistory], null)) : Task.FromException<TradePage>(TradesFail);
    }

    public Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public OrderBookDto? BookAnswer { get; set; }

    public Exception? BookFails { get; set; }

    public List<(string Ticker, int? Depth)> BookQueries { get; } = [];

    public Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default)
    {
        BookQueries.Add((ticker, depth));
        return BookFails is not null ? Task.FromException<OrderBookDto>(BookFails) : Task.FromResult((BookAnswer ?? Samples.Book(ticker)) with { Ticker = ticker });
    }

    public Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 23, 0, 0, TimeSpan.Zero);

    public const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    public static StatusDto Status(params string[] problems) =>
        new(
            "2026-10-18T23:00:00.000000+00:00",
            problems,
            new IngestStateDto("running", "2026-10-18T22:59:57.000000+00:00", 3),
            new StreamsDto(new StreamDto("2026-10-18T22:59:59.000000+00:00", 1), new StreamDto("2026-10-18T22:59:58.000000+00:00", 2)),
            new StorageDto(276_824_064, 107_374_182_400, 988_000_000_000, 264_000_000, 0, "2026-10-17", "ok used=0% dropped=", 40),
            new Dictionary<string, long> { ["active"] = 120018, ["closed"] = 288, ["unlisted"] = 428 },
            new GapSummaryDto(24, 2, new Dictionary<string, long> { ["ingest_down"] = 2 }, "Ticker updates in a gap are lost."),
            new BooksStatusDto(2, 0, 0, 3));

    public static ServerIdentity Identity(string fingerprint = Fingerprint) =>
        new(new CertificateInfo("CN=kaldb, O=KalTerm", fingerprint, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 10, 18, 0, 0, 0, DateTimeKind.Utc), ["kaldb", "192.168.1.20"]));

    public const string Token = "kt_0123456789abcdef0123456789abcdef0123456789a";

    public static ApiException Unauthorized() => new(HttpStatusCode.Unauthorized, "unauthorized");

    public static ScreenerRowDto Row(string ticker, string? last = "0.560000", string? bid = "0.550000", string? ask = "0.570000", string? spread = "0.020000", string? change = "0.020000", string volume = "1234.00", string? openInterest = "99.00", string status = "active", string label = "Yes", string title = "", string eventTitle = "", string eventSubTitle = "") =>
        new(ticker, "EV-" + ticker, status, label, "2026-10-18T22:58:00.000000+00:00", "2026-10-18T22:59:00.000000+00:00", last, change, volume, 7, openInterest, bid, ask, spread, null, null, null, null, false, title, eventTitle, eventSubTitle);

    public static TickDto Tick(string time = "2026-10-18T22:59:30.250000+00:00", string? price = "0.570000", string? bid = "0.560000", string? ask = "0.580000", string? openInterest = "100.00") =>
        new(time, price, bid, ask, "5.00", "6.00", "5000.00", openInterest, null);

    /// <summary>A candle that starts the given number of hours after 2026-10-20 00:00 UTC, with the given prices; null prices make a candle with no trades.</summary>
    public static CandleDto Candle(int hour, string? open = "0.500000", string? high = "0.550000", string? low = "0.450000", string? close = "0.520000", string volume = "100.00", bool partial = false, string? gapKind = null, int? gapSeconds = null, int trades = 7) =>
        new(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(hour).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture), open, high, low, close, volume, trades, "0.510000", "0.530000", "99.00", partial, partial, false, gapKind, gapSeconds);

    /// <summary>A book: yes bids best first, no bids best first (so the yes asks are 1 minus those).</summary>
    public static OrderBookDto Book(string ticker, bool stale = false, bool approximate = false, params (string Side, string Price, string Quantity)[] levels)
    {
        var all = levels.Length > 0
            ? levels
            : [("yes", "0.550000", "10.00"), ("yes", "0.540000", "20.50"), ("yes", "0.530000", "5.00"), ("no", "0.430000", "7.00"), ("no", "0.420000", "12.00"), ("no", "0.400000", "1.00")];
        return new OrderBookDto(
            ticker,
            "2026-10-18T22:59:58.000000+00:00",
            "2026-10-18T22:59:59.000000+00:00",
            1,
            stale,
            approximate,
            [.. all.Where(l => l.Side == "yes").Select(l => new BookLevelDto(l.Price, l.Quantity))],
            [.. all.Where(l => l.Side == "no").Select(l => new BookLevelDto(l.Price, l.Quantity))]);
    }

    public static MarketTickDto MarketTick(string ticker, TickDto? tick = null) => new(ticker, tick ?? Tick());

    public static TradeDto Trade(string id, string time = "2026-10-18T22:59:00.000000+00:00", string side = "yes", bool block = false) => new(time, id, "0.560000", "0.440000", "3.00", side, block);

    public static MarketDto Market(string ticker, string title = "Washington wins", string eventTitle = "Washington vs San Francisco", string eventSubTitle = "WAS vs SF (Oct 18)") =>
        new(ticker, "EV-" + ticker, "binary", "active", "Washington", "San Francisco", "2026-10-18T14:00:00.000000+00:00", "2026-10-19T04:00:00.000000+00:00", "2026-10-26T04:00:00.000000+00:00", string.Empty, null, null, "2026-10-17T00:00:00.000000+00:00", "2026-10-18T22:00:00.000000+00:00", title, eventTitle, eventSubTitle);

    public static MetricsDto Metrics(string ticker) =>
        new(ticker, "2026-10-18T22:58:00.000000+00:00", "2026-10-18T22:59:00.000000+00:00", "0.560000", "0.020000", "1234.00", 7, "99.00", "0.550000", "0.570000", "0.020000", "0.551000", "0.540000", "0.530000", "0.012000", false);
}

/// <summary>The terminal's services built from fakes and stores in a folder of the test's own.</summary>
internal sealed class TestServices : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-terminal-test-" + Guid.NewGuid().ToString("N"));

    public TestServices()
    {
        Pins = new PinStore(Path.Combine(_folder, "known-hosts.json"));
        Tokens = new TokenStore(Path.Combine(_folder, "credentials.bin"));
        Settings = new TerminalSettingsStore(Path.Combine(_folder, "terminal.json"));
        Services = new ClientServices
        {
            Pins = Pins,
            Tokens = Tokens,
            Probe = (host, port, _) =>
            {
                Probed.Add((host, port));
                return ProbeFails is null ? Task.FromResult(Identity) : Task.FromException<ServerIdentity>(ProbeFails);
            },
            CreateApi = address =>
            {
                Created.Add(address);
                return CreateApiFails is null ? Api : throw CreateApiFails;
            },
            CreateApiWithToken = (address, token) =>
            {
                TokenTries.Add((address, token));
                return TokenApi;
            },
            Time = Time,
            SystemZone = SystemZone,
        };
    }

    public PinStore Pins { get; }

    public TokenStore Tokens { get; }

    public TerminalSettingsStore Settings { get; }

    public ClientServices Services { get; }

    public FakeApi Api { get; } = new();

    /// <summary>The client that carries a token being tried; refuses it when <see cref="FakeApi.MarketsFail"/> is set.</summary>
    public FakeApi TokenApi { get; } = new();

    public ServerIdentity Identity { get; set; } = Samples.Identity();

    public Exception? ProbeFails { get; set; }

    public Exception? CreateApiFails { get; set; }

    public List<(string Host, int Port)> Probed { get; } = [];

    public List<Uri> Created { get; } = [];

    public List<(Uri Address, string Token)> TokenTries { get; } = [];

    public AutoAdvanceTimeProvider Time { get; } = new(autoFire: false, utcStart: Samples.Now);

    public TimeZoneInfo SystemZone { get; set; } = TimeZoneInfo.Utc;

    public static string Key => "kaldb:5280";

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

/// <summary>Dialogs a test controls: what the connect dialog returns, and what it was shown.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public List<ConnectViewModel> Shown { get; } = [];

    public Func<ConnectViewModel, Uri?> Answer { get; set; } = _ => null;

    /// <summary>When set, the dialog stays open until the test completes it.</summary>
    public TaskCompletionSource<Uri?>? Pending { get; set; }

    public Task<Uri?> ShowConnectAsync(ConnectViewModel connect)
    {
        Shown.Add(connect);
        return Pending is { } pending ? pending.Task : Task.FromResult(Answer(connect));
    }
}
