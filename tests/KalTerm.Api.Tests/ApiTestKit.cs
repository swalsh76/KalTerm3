using KalTerm.Contracts;
using KalTerm.Data;
using KalTerm.Data.Alerts;
using KalTerm.Data.Auth;
using KalTerm.Data.Books;
using KalTerm.Data.Candles;
using KalTerm.Data.Metrics;
using KalTerm.Data.Reading;
using KalTerm.Data.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 23, 0, 0, TimeSpan.Zero);
    public static readonly DateTime NowUtc = Now.UtcDateTime;

    public static StoredMarket Market(string ticker, string status = "active") =>
        new(ticker, "EV-" + ticker, "binary", status, "Yes", "No", NowUtc.AddHours(-5), NowUtc.AddDays(1), null, null, string.Empty, null, NowUtc.AddHours(-4), NowUtc.AddMinutes(-1));

    public static StoredTick Tick(DateTime ts, long price = 560_000) => new(ts, price, price - 10_000, price + 10_000, 1_000, 2_050, 123_400, 9_900, null);

    public static StoredBook Book(string ticker, bool stale = false, bool approximate = false, int levels = 3) => new(
        ticker,
        NowUtc.AddSeconds(-10),
        NowUtc.AddSeconds(-2),
        stale,
        approximate,
        [.. Enumerable.Range(0, levels).Select(i => new BookLevel(410_000 - (i * 10_000), 150 + i))],
        [.. Enumerable.Range(0, levels).Select(i => new BookLevel(550_000 - (i * 10_000), 200))]);

    public static StoredTrade Trade(DateTime ts, string id, byte side = 1, bool block = false) => new(ts, id, 400_000, 600_000, 300, side, block);
}

internal sealed class FakeMarkets : IMarketReader
{
    public List<(MarketFilter Filter, MarketPosition? After, int Limit)> Lists { get; } = [];

    public Page<StoredMarket, MarketPosition> Page { get; set; } = new([], null);

    public Dictionary<string, StoredMarket> ByTicker { get; } = new(StringComparer.Ordinal);

    public Exception? Throw { get; set; }

    public Task<Page<StoredMarket, MarketPosition>> ListAsync(MarketFilter filter, MarketPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        Lists.Add((filter, after, limit));
        return Throw is null ? Task.FromResult(Page) : Task.FromException<Page<StoredMarket, MarketPosition>>(Throw);
    }

    public Task<StoredMarket?> GetAsync(string ticker, CancellationToken cancellationToken = default) =>
        Throw is null ? Task.FromResult(ByTicker.GetValueOrDefault(ticker)) : Task.FromException<StoredMarket?>(Throw);
}

internal sealed class FakeTicks : ITickReader
{
    public List<(string Ticker, DateTime? Start, DateTime End, TickPosition? After, int Limit)> Reads { get; } = [];

    public Page<StoredTick, TickPosition>? Page { get; set; } = new([], null);

    public Task<Page<StoredTick, TickPosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TickPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Add((ticker, start, until, after, limit));
        return Task.FromResult(Page);
    }
}

internal sealed class FakeTrades : ITradeReader
{
    public List<(string Ticker, DateTime? Start, DateTime End, TradePosition? After, int Limit)> Reads { get; } = [];

    public Page<StoredTrade, TradePosition>? Page { get; set; } = new([], null);

    public Task<Page<StoredTrade, TradePosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TradePosition? after, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Add((ticker, start, until, after, limit));
        return Task.FromResult(Page);
    }
}

internal sealed class FakeGaps : IGapReader
{
    public List<(DateTime Start, DateTime End, int Limit)> Reads { get; } = [];

    public IReadOnlyList<StoredGap> Gaps { get; set; } = [];

    public Task<IReadOnlyList<StoredGap>> ReadAsync(DateTime start, DateTime until, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Add((start, until, limit));
        return Task.FromResult(Gaps);
    }
}

internal sealed class FakeBooks : IBookReader
{
    public List<StoredBook> Books { get; } = [];

    public Task<IReadOnlyList<StoredBook>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StoredBook>>([.. Books]);

    public Task<StoredBook?> GetAsync(string ticker, CancellationToken cancellationToken = default) =>
        Task.FromResult(Books.FirstOrDefault(b => b.Ticker == ticker));

    public Exception? Throw { get; set; }

    public int ManyReads { get; private set; }

    public Task<IReadOnlyList<StoredBook>> GetManyAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        ManyReads++;
        return Throw is null ? Task.FromResult<IReadOnlyList<StoredBook>>([.. Books.Where(b => tickers.Contains(b.Ticker)).OrderBy(b => b.Ticker, StringComparer.Ordinal)]) : Task.FromException<IReadOnlyList<StoredBook>>(Throw);
    }
}

/// <summary>The market stream reader over lists a test fills: which tickers are markets, and the ticker rows in the table.</summary>
internal sealed class FakeStreamReader : IMarketStreamReader
{
    public Dictionary<string, int> Markets { get; } = new(StringComparer.Ordinal) { ["KX-1"] = 1, ["KX-2"] = 2, ["KX-3"] = 3 };

    public List<StreamTick> Rows { get; } = [];

    public List<(DateTime From, DateTime Until, int Limit, int[] Ids)> Reads { get; } = [];

    public int Resolves { get; private set; }

    public Exception? Throw { get; set; }

    public Task<IReadOnlyDictionary<string, int>> ResolveAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        Resolves++;
        return Throw is null
            ? Task.FromResult<IReadOnlyDictionary<string, int>>(tickers.Where(Markets.ContainsKey).ToDictionary(t => t, t => Markets[t], StringComparer.Ordinal))
            : Task.FromException<IReadOnlyDictionary<string, int>>(Throw);
    }

    public Task<IReadOnlyList<StreamTick>> ReadTicksAsync(IReadOnlyDictionary<int, string> markets, DateTime from, DateTime until, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Add((from, until, limit, [.. markets.Keys.Order()]));
        if (Throw is not null)
        {
            return Task.FromException<IReadOnlyList<StreamTick>>(Throw);
        }

        return Task.FromResult<IReadOnlyList<StreamTick>>([.. Rows.Where(r => markets.Values.Contains(r.Ticker) && r.Tick.Ts >= from && r.Tick.Ts < until).OrderBy(r => r.Tick.Ts).Take(limit)]);
    }
}

internal sealed class FakeCandles : ICandleService
{
    public List<CandleQuery> Queries { get; } = [];

    public CandleResult? Result { get; set; } = new([], "stored 1h", null);

    public Exception? Throw { get; set; }

    public Task<CandleResult?> GetAsync(CandleQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Throw is null ? Task.FromResult(Result) : Task.FromException<CandleResult?>(Throw);
    }
}

internal sealed class FakeMetrics : IMetricsReader
{
    public List<(string Ticker, DateTime AsOf)> Reads { get; } = [];

    public MetricsInputs? Inputs { get; set; } = new(Samples.NowUtc, Samples.NowUtc.AddSeconds(-3), 560_000, 550_000, 570_000, 9_900, 2, 400, 220_000_000, 540_000, [500_000, 400_000, 450_000], false);

    public Task<MetricsInputs?> ReadAsync(string ticker, DateTime asOf, CancellationToken cancellationToken = default)
    {
        Reads.Add((ticker, asOf));
        return Task.FromResult(Inputs);
    }
}

internal sealed class FakeScreener : IScreenerReader
{
    public List<ScreenerQuery> Queries { get; } = [];

    public ScreenerResult Result { get; set; } = new([], null, null);

    public Task<ScreenerResult> ReadAsync(ScreenerQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Task.FromResult(Result);
    }
}

/// <summary>The user data store in memory: it remembers every call (with the user id it was given) and answers from what a test set.</summary>
internal sealed class FakeUserData : IUserDataStore
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<WatchlistSummary> Lists { get; set; } = [];

    public WatchlistDetail? Detail { get; set; }

    public StoreOutcome Outcome { get; set; } = StoreOutcome.Ok;

    public IReadOnlyList<UserAlert> Alerts { get; set; } = [];

    public IReadOnlyList<UserFire> Fires { get; set; } = [];

    public IReadOnlyList<int>? MarketIds { get; set; } = [1, 2];

    public string? Setting { get; set; }

    public int NextAlertId { get; set; } = 5;

    public Task<IReadOnlyList<WatchlistSummary>> ListWatchlistsAsync(int userId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"list-watchlists {userId}");
        return Task.FromResult(Lists);
    }

    public Task<WatchlistDetail?> GetWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"get-watchlist {userId} {name}");
        return Task.FromResult(Detail);
    }

    public Task<StoreOutcome> CreateWatchlistAsync(int userId, string name, DateTime now, CancellationToken cancellationToken = default)
    {
        Calls.Add($"create-watchlist {userId} {name}");
        return Task.FromResult(Outcome);
    }

    public Task<StoreOutcome> DeleteWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete-watchlist {userId} {name}");
        return Task.FromResult(Outcome);
    }

    public Task<StoreOutcome> AddMarketAsync(int userId, string name, string ticker, DateTime now, CancellationToken cancellationToken = default)
    {
        Calls.Add($"add-market {userId} {name} {ticker}");
        return Task.FromResult(Outcome);
    }

    public Task<StoreOutcome> RemoveMarketAsync(int userId, string name, string ticker, CancellationToken cancellationToken = default)
    {
        Calls.Add($"remove-market {userId} {name} {ticker}");
        return Task.FromResult(Outcome);
    }

    public Task<IReadOnlyList<int>?> WatchlistMarketIdsAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"market-ids {userId} {name}");
        return Task.FromResult(MarketIds);
    }

    public Task<IReadOnlyList<UserAlert>> ListAlertsAsync(int userId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"list-alerts {userId}");
        return Task.FromResult(Alerts);
    }

    public Task<(StoreOutcome Outcome, int? AlertId)> AddAlertAsync(int userId, string ticker, AlertKind kind, long threshold, int cooldownSeconds, DateTime now, CancellationToken cancellationToken = default)
    {
        Calls.Add($"add-alert {userId} {ticker} {AlertKinds.Name(kind)} {threshold} {cooldownSeconds}");
        if (Outcome == StoreOutcome.Ok)
        {
            Alerts = [.. Alerts, new UserAlert(NextAlertId, ticker, kind, threshold, cooldownSeconds, false, now, null, null)];
        }

        return Task.FromResult((Outcome, Outcome == StoreOutcome.Ok ? (int?)NextAlertId : null));
    }

    public Task<StoreOutcome> DeleteAlertAsync(int userId, int alertId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete-alert {userId} {alertId}");
        return Task.FromResult(Outcome);
    }

    public Task<StoreOutcome> SetAlertPausedAsync(int userId, int alertId, bool paused, CancellationToken cancellationToken = default)
    {
        Calls.Add($"pause-alert {userId} {alertId} {paused}");
        return Task.FromResult(Outcome);
    }

    public Task<IReadOnlyList<UserFire>> ReadFiresAsync(int userId, long? afterFireId, int limit, CancellationToken cancellationToken = default)
    {
        Calls.Add($"fires {userId} {afterFireId} {limit}");
        IEnumerable<UserFire> matching = afterFireId is { } after ? Fires.Where(f => f.FireId > after) : Fires.TakeLast(limit);
        return Task.FromResult<IReadOnlyList<UserFire>>([.. matching.Take(limit)]);
    }

    public IReadOnlyList<LayoutSummary> LayoutList { get; set; } = [];

    public LayoutDetail? Layout { get; set; }

    public Task<IReadOnlyList<LayoutSummary>> ListLayoutsAsync(int userId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"list-layouts {userId}");
        return Task.FromResult(LayoutList);
    }

    public Task<LayoutDetail?> GetLayoutAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"get-layout {userId} {name}");
        return Task.FromResult(Layout);
    }

    public Task<StoreOutcome> PutLayoutAsync(int userId, string name, string body, DateTime now, CancellationToken cancellationToken = default)
    {
        Calls.Add($"put-layout {userId} {name} {body}");
        return Task.FromResult(Outcome);
    }

    public Task<StoreOutcome> DeleteLayoutAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete-layout {userId} {name}");
        return Task.FromResult(Outcome);
    }

    public Task<string?> GetSettingAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        Calls.Add($"get-setting {userId} {name}");
        return Task.FromResult(Setting);
    }

    public Task SetSettingAsync(int userId, string name, string? value, DateTime now, CancellationToken cancellationToken = default)
    {
        Calls.Add($"set-setting {userId} {name} {value ?? "(clear)"}");
        Setting = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeVersion : IAlertVersionReader
{
    public long Value { get; set; }

    public Exception? Fails { get; set; }

    public Task<long> ReadAsync(CancellationToken cancellationToken = default) => Fails is null ? Task.FromResult(Value) : Task.FromException<long>(Fails);
}

internal sealed class FakeStatus : IStatusReader
{
    public StatusSnapshot Snapshot { get; set; } = new(null, null, null, 1_000, null, null, new Dictionary<string, long>(), new Dictionary<string, long>());

    public DateTime? AskedAt { get; private set; }

    public Task<StatusSnapshot> ReadAsync(DateTime now, TimeSpan gapWindow, CancellationToken cancellationToken = default)
    {
        AskedAt = now;
        return Task.FromResult(Snapshot);
    }
}

internal sealed class FakeReadiness : IReadinessProbe
{
    public bool Ready { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Ready);
}

/// <summary>All the fake readers, registered together.</summary>
internal sealed class FakeReaders
{
    public FakeReaders(MarketStreamOptions? streamOptions = null)
    {
        Hub = new AlertStreamHub(Version, new AlertStreamOptions { MaxPerUser = 2, MaxTotal = 3, KeepAlive = TimeSpan.FromMilliseconds(150) });
        StreamHub = new MarketStreamHub(StreamReader, Books, StreamTime, streamOptions ?? new MarketStreamOptions { MaxPerUser = 2, MaxTotal = 3, MaxMarkets = 3, KeepAlive = TimeSpan.FromMilliseconds(150) });
    }

    public FakeStreamReader StreamReader { get; } = new();

    /// <summary>The clock the market streams use (the ticker table is read up to two seconds behind it).</summary>
    public AutoAdvanceTimeProvider StreamTime { get; } = new(autoFire: false, utcStart: Samples.Now);

    public MarketStreamHub StreamHub { get; }

    public FakeMarkets Markets { get; } = new();

    public FakeTicks Ticks { get; } = new();

    public FakeTrades Trades { get; } = new();

    public FakeGaps Gaps { get; } = new();

    public FakeBooks Books { get; } = new();

    public FakeCandles Candles { get; } = new();

    public FakeMetrics Metrics { get; } = new();

    public FakeScreener Screener { get; } = new();

    public FakeUserData UserData { get; } = new();

    public FakeVersion Version { get; } = new();

    public AlertStreamHub Hub { get; }

    public FakeStatus Status { get; } = new();

    public FakeReadiness Readiness { get; } = new();

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IMarketReader>(Markets);
        services.AddSingleton<ITickReader>(Ticks);
        services.AddSingleton<ITradeReader>(Trades);
        services.AddSingleton<IGapReader>(Gaps);
        services.AddSingleton<IBookReader>(Books);
        services.AddSingleton<ICandleService>(Candles);
        services.AddSingleton<IMetricsReader>(Metrics);
        services.AddSingleton<IScreenerReader>(Screener);
        services.AddSingleton<IUserDataStore>(UserData);
        services.AddSingleton<IAlertVersionReader>(Version);
        services.AddSingleton(Hub);
        services.AddSingleton<IMarketStreamReader>(StreamReader);
        services.AddSingleton(StreamHub);
        services.AddSingleton<IStatusReader>(Status);
        services.AddSingleton<IReadinessProbe>(Readiness);
        services.AddSingleton(new ApiAuthOptions(false));
    }
}

/// <summary>The API in memory: the real pipeline and endpoints on a test server, over fake readers or over a real database.</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiTestHost(WebApplication app, FakeReaders fakes)
    {
        _app = app;
        Fakes = fakes;
        Client = app.GetTestClient();
    }

    public FakeReaders Fakes { get; }

    public HttpClient Client { get; }

    public static async Task<ApiTestHost> StartAsync()
    {
        var fakes = new FakeReaders();
        return await BuildAsync(fakes.Register, fakes, new AutoAdvanceTimeProvider(autoFire: false, utcStart: Samples.Now));
    }

    /// <summary>The fake readers, with every request made as a given user (what the sign-in check would have set after a good token).</summary>
    public static async Task<ApiTestHost> StartAsUserAsync(int userId = 42, MarketStreamOptions? stream = null)
    {
        var fakes = new FakeReaders(stream);
        return await BuildAsync(
            fakes.Register,
            fakes,
            new AutoAdvanceTimeProvider(autoFire: false, utcStart: Samples.Now),
            app => app.Use(async (context, next) =>
            {
                context.Items[ApiAuthentication.UserItem] = new AuthenticatedUser(userId, "tester", "read", 1);
                await next(context);
            }));
    }

    /// <summary>With tokens required, a fake user store, a controllable clock and a hook that runs before the API (to set the caller's addresses).</summary>
    public static async Task<ApiTestHost> StartWithAuthAsync(IUserStore users, AutoAdvanceTimeProvider time, Action<HttpContext>? before = null)
    {
        var fakes = new FakeReaders();
        return await BuildAsync(
            services =>
            {
                fakes.Register(services);
                services.AddSingleton(users);
                services.AddSingleton(new ApiAuthOptions(true));
                services.AddSingleton<AuthThrottle>();
                services.AddSingleton(provider => new TokenAuthenticator(provider.GetRequiredService<IUserStore>(), provider.GetRequiredService<TimeProvider>()));
            },
            fakes,
            time,
            before is null ? null : app => app.Use(async (context, next) =>
            {
                before(context);
                await next(context);
            }));
    }

    /// <summary>Over a real database; the clock is the real one, because the rows in it are stamped with real times.</summary>
    public static Task<ApiTestHost> StartWithDatabaseAsync(DataSource source) => BuildAsync(services => services.AddKalTermApi(source, null, requireToken: false), new FakeReaders(), TimeProvider.System);

    /// <summary>Over a real database, with every request made as a given user (for the streams, which belong to a user).</summary>
    public static Task<ApiTestHost> StartWithDatabaseAsUserAsync(DataSource source, int userId = 42) => BuildAsync(
        services => services.AddKalTermApi(source, null, requireToken: false),
        new FakeReaders(),
        TimeProvider.System,
        app => app.Use(async (context, next) =>
        {
            context.Items[ApiAuthentication.UserItem] = new AuthenticatedUser(userId, "tester", "read", 1);
            await next(context);
        }));

    public async Task<(HttpResponseMessage Response, string Body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public async Task<(HttpResponseMessage Response, string Body)> GetAsync(string url)
    {
        var response = await Client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public async Task<T> GetAsync<T>(string url)
        where T : class
    {
        var (response, body) = await GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url} gave {(int)response.StatusCode}: {body}");
        return ApiJson.Deserialize<T>(body)!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task<ApiTestHost> BuildAsync(Action<IServiceCollection> register, FakeReaders fakes, TimeProvider time, Action<WebApplication>? before = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        register(builder.Services);
        builder.Services.AddSingleton(time);
        var app = builder.Build();
        before?.Invoke(app);
        app.UseKalTermApi();
        await app.StartAsync();
        return new ApiTestHost(app, fakes);
    }
}
