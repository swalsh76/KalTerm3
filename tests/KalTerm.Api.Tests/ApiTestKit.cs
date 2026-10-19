using KalTerm.Contracts;
using KalTerm.Data;
using KalTerm.Data.Books;
using KalTerm.Data.Reading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    public FakeMarkets Markets { get; } = new();

    public FakeTicks Ticks { get; } = new();

    public FakeTrades Trades { get; } = new();

    public FakeGaps Gaps { get; } = new();

    public FakeBooks Books { get; } = new();

    public FakeStatus Status { get; } = new();

    public FakeReadiness Readiness { get; } = new();

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IMarketReader>(Markets);
        services.AddSingleton<ITickReader>(Ticks);
        services.AddSingleton<ITradeReader>(Trades);
        services.AddSingleton<IGapReader>(Gaps);
        services.AddSingleton<IBookReader>(Books);
        services.AddSingleton<IStatusReader>(Status);
        services.AddSingleton<IReadinessProbe>(Readiness);
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

    /// <summary>Over a real database; the clock is the real one, because the rows in it are stamped with real times.</summary>
    public static Task<ApiTestHost> StartWithDatabaseAsync(DataSource source) => BuildAsync(services => services.AddKalTermApi(source), new FakeReaders(), TimeProvider.System);

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

    private static async Task<ApiTestHost> BuildAsync(Action<IServiceCollection> register, FakeReaders fakes, TimeProvider time)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        register(builder.Services);
        builder.Services.AddSingleton(time);
        var app = builder.Build();
        app.UseKalTermApi();
        await app.StartAsync();
        return new ApiTestHost(app, fakes);
    }
}
