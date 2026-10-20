using KalTerm.Client;
using KalTerm.Contracts;
using Spectre.Console.Testing;

namespace KalTerm.Cli.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 23, 0, 0, TimeSpan.Zero);

    public static MarketDto Market(string ticker, string status = "active") =>
        new(ticker, "EV-" + ticker, "binary", status, "Yes", "No", "2026-10-18T14:00:00.000000+00:00", "2026-10-19T23:00:00.000000+00:00", null, string.Empty, null, null, "2026-10-18T22:23:30.123000+00:00", "2026-10-18T22:53:30.000000+00:00");

    public static TickDto Tick(string time = "2026-10-18T22:59:59.250000+00:00") =>
        new(time, "0.560000", "0.550000", "0.570000", "10.00", "20.50", "1234567.00", "99.00", null);

    public static TradeDto Trade(string id = "t-1", string? side = "yes", bool block = false) =>
        new("2026-10-18T22:59:58.500000+00:00", id, "0.400000", "0.600000", "3.00", side, block);

    public static OrderBookDto Book(string ticker, bool stale = false, bool approximate = false) =>
        new(
            ticker,
            "2026-10-18T22:59:50.250000+00:00",
            "2026-10-18T22:59:58.000000+00:00",
            2,
            stale,
            approximate,
            [new BookLevelDto("0.410000", "1.50"), new BookLevelDto("0.400000", "1500.00")],
            [new BookLevelDto("0.550000", "2.00"), new BookLevelDto("0.540000", "7.00")]);

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
}

/// <summary>An API that answers with what a test gave it, remembers what it was asked, and can fail.</summary>
internal sealed class FakeApi : IKalTermApi, IDisposable
{
    public StatusDto Status { get; set; } = Samples.Status();

    public MarketPage Markets { get; set; } = new([Samples.Market("A-1")], null);

    public MarketDto? Market { get; set; } = Samples.Market("A-1");

    public TickPage Ticks { get; set; } = new([Samples.Tick()], null);

    public TradePage Trades { get; set; } = new([Samples.Trade()], null);

    public CandlePage Candles { get; set; } = new("1h", "stored 1h", [], null);

    public ScreenerPage Screener { get; set; } = new("volume", "desc", "2026-10-18T22:55:00.000000+00:00", 300, [], null);

    public List<ScreenQuery> ScreenQueries { get; } = [];

    public MetricsDto Metrics { get; set; } = new("A-1", "2026-10-18T23:00:00.000000+00:00", "2026-10-18T22:59:59.250000+00:00", "0.560000", "0.020000", "1234.00", 7, "99.00", "0.550000", "0.570000", "0.020000", "0.555000", "0.540000", "0.500000", "0.012000", false);

    public WatchlistListDto Watchlists { get; set; } = new([new WatchlistSummaryDto("favorites", 2, "2026-10-18T20:00:00.000000+00:00")]);

    public WatchlistDto Watchlist { get; set; } = new("favorites", "2026-10-18T20:00:00.000000+00:00", ["A-1", "B-2"]);

    public AlertListDto Alerts { get; set; } = new([new AlertDto(7, "A-1", "price_above", "0.600000", 60, false, "2026-10-18T20:00:00.000000+00:00", null, null)]);

    public AlertFiresPage Fires { get; set; } = new([new AlertFireDto(41, 7, "A-1", "price_above", "0.600000", "0.610000", "2026-10-18T22:59:00.250000+00:00")], 41);

    public string? StoredTimeZone { get; set; }

    /// <summary>What each opening of the alert stream gives: a list of events, or an exception. When the script has run out the stream cancels the run (the way a person would press Ctrl+C).</summary>
    public Queue<object> StreamScript { get; } = new();

    public List<long?> StreamOpens { get; } = [];

    public CancellationTokenSource? StopWhenScriptEnds { get; set; }

    public List<string> Changes { get; } = [];

    public GapPage Gaps { get; set; } = new([]);

    public OrderBookPage Books { get; set; } = new([Samples.Book("A-1")]);

    public OrderBookDto? Book { get; set; } = Samples.Book("A-1");

    public List<int?> BooksDepths { get; } = [];

    public List<(string Ticker, int? Depth)> BookQueries { get; } = [];

    public Exception? Throw { get; set; }

    public bool Disposed { get; private set; }

    public MarketsQuery? MarketsQuery { get; private set; }

    public List<(string Ticker, WindowQuery Query)> TickQueries { get; } = [];

    public List<(string Ticker, WindowQuery Query)> TradeQueries { get; } = [];

    public List<(string Ticker, string Interval, WindowQuery Query)> CandleQueries { get; } = [];

    public WindowQuery? GapQuery { get; private set; }

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Answer(Status);

    public Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default)
    {
        MarketsQuery = query;
        return Answer(Markets);
    }

    public Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default) =>
        Market is null ? Task.FromException<MarketDto>(new ApiException(System.Net.HttpStatusCode.NotFound, "not_found")) : Answer(Market);

    public Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        TickQueries.Add((ticker, query));
        return Answer(Ticks);
    }

    public Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        TradeQueries.Add((ticker, query));
        return Answer(Trades);
    }

    public Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default)
    {
        CandleQueries.Add((ticker, interval, query));
        return Answer(Candles);
    }

    public Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default)
    {
        ScreenQueries.Add(query);
        return Answer(Screener);
    }

    public Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default) => Answer(Watchlists);

    public Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default) => Answer(Watchlist);

    public Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default)
    {
        Changes.Add("create-watchlist " + name);
        return Answer(new WatchlistDto(name, "2026-10-18T23:00:00.000000+00:00", []));
    }

    public Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default)
    {
        Changes.Add("delete-watchlist " + name);
        return Answer(0);
    }

    public Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default)
    {
        Changes.Add($"add {name} {ticker}");
        return Answer(0);
    }

    public Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default)
    {
        Changes.Add($"remove {name} {ticker}");
        return Answer(0);
    }

    public Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default) => Answer(Alerts);

    public Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default)
    {
        Changes.Add($"create-alert {request.Ticker} {request.Kind} {request.Threshold} {request.CooldownSeconds}");
        return Answer(new AlertDto(8, request.Ticker, request.Kind, request.Threshold, request.CooldownSeconds ?? 60, false, "2026-10-18T23:00:00.000000+00:00", null, null));
    }

    public Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default)
    {
        Changes.Add($"{(paused ? "pause" : "resume")} {id}");
        return Answer(0);
    }

    public Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default)
    {
        Changes.Add("delete-alert " + id);
        return Answer(0);
    }

    public Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        Changes.Add($"fires {after} {limit}");
        return Answer(Fires);
    }

    /// <summary>What each opening of the market stream gives: a list of events, or an exception. When the script has run out the stream cancels the run.</summary>
    public Queue<object> MarketScript { get; } = new();

    public List<MarketStreamRequest> MarketOpens { get; } = [];

    public async IAsyncEnumerable<MarketStreamEvent> StreamMarketsAsync(MarketStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MarketOpens.Add(request);
        if (!MarketScript.TryDequeue(out var step))
        {
            StopWhenScriptEnds?.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        if (step is Exception failure)
        {
            throw failure;
        }

        foreach (var item in (IEnumerable<MarketStreamEvent>)step)
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamOpens.Add(after);
        if (!StreamScript.TryDequeue(out var step))
        {
            StopWhenScriptEnds?.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        if (step is Exception failure)
        {
            throw failure;
        }

        foreach (var item in (IReadOnlyList<AlertStreamEvent>)step)
        {
            yield return item;
        }
    }

    public Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingDto("time_zone", StoredTimeZone));

    public Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default)
    {
        Changes.Add("tz " + (zone ?? "(clear)"));
        StoredTimeZone = zone;
        return Answer(0);
    }

    public Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default) => Answer(Metrics);

    public Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default)
    {
        GapQuery = query;
        return Answer(Gaps);
    }

    public Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default)
    {
        BooksDepths.Add(depth);
        return Answer(Books);
    }

    public Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default)
    {
        BookQueries.Add((ticker, depth));
        return Book is null ? Task.FromException<OrderBookDto>(new ApiException(System.Net.HttpStatusCode.NotFound, "not_found")) : Answer(Book);
    }

    public void Dispose() => Disposed = true;

    private Task<T> Answer<T>(T value) => Throw is null ? Task.FromResult(value) : Task.FromException<T>(Throw);
}

/// <summary>The CLI with a fake API, test consoles and a fixed clock.</summary>
internal sealed class Harness : IDisposable
{
    private readonly TestConsole _out = new TestConsole().Width(140);
    private readonly TestConsole _err = new TestConsole().Width(140);

    private readonly string _profile = Path.Combine(Path.GetTempPath(), "kalterm-cli-test-" + Guid.NewGuid().ToString("N"));
    private readonly Queue<string> _typed = new();

    public TimeZoneInfo SystemZone { get; set; } = TimeZoneInfo.Utc;

    public bool HasStoredToken { get; set; } = true;

    /// <summary>Whether waits (a reconnect pause) end at once instead of waiting for the test to fire them.</summary>
    public bool AutoFireTimers { get; set; }

    public Harness(Dictionary<string, string>? variables = null)
    {
        Variables = variables ?? [];
        Pins = new PinStore(Path.Combine(_profile, "known-hosts.json"));
        Tokens = new TokenStore(Path.Combine(_profile, "credentials.bin"));
    }

    public PinStore Pins { get; }

    public TokenStore Tokens { get; }

    /// <summary>What the server presents when <c>trust</c> reads its certificate.</summary>
    public ServerIdentity Identity { get; set; } = new(new KalTerm.Security.CertificateInfo("CN=kaldb, O=KalTerm", new string('A', 8) + new string('B', 56), new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 10, 18, 0, 0, 0, DateTimeKind.Utc), ["kaldb", "192.168.1.20"]));

    public Exception? ProbeFails { get; set; }

    public Func<KalTerm.Trading.Session.TradingMode, CancellationToken, Task<KalTerm.Trading.Session.TradingSession>>? OpenTrading { get; set; }

    public KalTerm.Trading.Keys.ITradingKeyVault? Vault { get; set; }

    public KalTerm.Trading.Account.TradingState? TradingState { get; set; }

    public bool Interactive { get; set; }

    public string Secret { get; set; } = string.Empty;

    public List<(string Host, int Port)> Probed { get; } = [];

    public List<(Uri Address, string Token)> TokenTries { get; } = [];

    public void Type(params string[] lines)
    {
        foreach (var line in lines)
        {
            _typed.Enqueue(line);
        }
    }

    public FakeApi Api { get; } = new();

    public Dictionary<string, string> Variables { get; }

    public Uri? ApiAddress { get; private set; }

    public int ClientsCreated { get; private set; }

    public string Output => _out.Output;

    public string Error => _err.Output;

    public int Width => _out.Profile.Width;

    public Task<int> RunAsync(params string[] args) => RunAsync(TestContext.Current.CancellationToken, args);

    public async Task<int> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var env = new CliEnvironment
        {
            Out = _out,
            Error = _err,
            CreateApi = address =>
            {
                ApiAddress = address;
                ClientsCreated++;
                return Api;
            },
            CreateApiWithToken = (address, token) =>
            {
                TokenTries.Add((address, token));
                return Api;
            },
            Pins = Pins,
            Tokens = Tokens,
            Probe = (host, port, _) =>
            {
                Probed.Add((host, port));
                return ProbeFails is null ? Task.FromResult(Identity) : Task.FromException<ServerIdentity>(ProbeFails);
            },
            InteractiveInput = Interactive,
            ReadLine = () => _typed.Count > 0 ? _typed.Dequeue() : null,
            ReadSecret = () => Secret,
            Time = new AutoAdvanceTimeProvider(autoFire: AutoFireTimers, utcStart: Samples.Now),
            HasToken = _ => HasStoredToken,
            GetVariable = name => Variables.GetValueOrDefault(name),
            SystemZone = SystemZone,
            OpenTrading = OpenTrading ?? ((_, _) => throw new InvalidOperationException("trading is not available here")),
            OpenVault = () => Vault ?? throw new InvalidOperationException("no vault"),
            LoadTradingState = () => TradingState ?? throw new InvalidOperationException("no state"),
        };
        return await CliApp.RunAsync(args, env, cancellationToken);
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }
    }
}
