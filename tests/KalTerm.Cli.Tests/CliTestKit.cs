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

    public async Task<int> RunAsync(params string[] args)
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
            Time = new AutoAdvanceTimeProvider(autoFire: false, utcStart: Samples.Now),
            GetVariable = name => Variables.GetValueOrDefault(name),
        };
        return await CliApp.RunAsync(args, env, TestContext.Current.CancellationToken);
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
