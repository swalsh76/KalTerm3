using System.Net;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Trading.Account;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Session;
using KalTerm.Trading.Tests.Keys;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Tests.Session;

/// <summary>The real Kalshi clients, session factory and order service, against scripted HTTP and a scripted socket: nothing leaves the process.</summary>
public sealed class TradingSessionsTests : IDisposable
{
    private const string Limits = """{"usage_tier":"basic","read":{"bucket_capacity":600,"refill_rate":200},"write":{"bucket_capacity":100,"refill_rate":100},"grants":[]}""";
    private const string Costs = """{"default_cost":10,"endpoint_costs":[{"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders/:order_id","cost":2}]}""";
    private const string Status = """{"exchange_active":true,"trading_active":true,"exchange_index_statuses":[{"exchange_index":0,"description":"s0","exchange_active":true,"trading_active":true,"intra_exchange_transfers_active":true},{"exchange_index":3,"description":"s3","exchange_active":true,"trading_active":true,"intra_exchange_transfers_active":true}]}""";
    private const string Balance = """{"balance":1034,"balance_dollars":"10.3490","balance_breakdown":[{"exchange_index":0,"balance":"10.3490"},{"exchange_index":3,"balance":"0.0000"}]}""";
    private const string Positions = """{"market_positions":[],"event_positions":[],"cursor":""}""";
    private const string NoOrders = """{"orders":[],"cursor":""}""";
    private const string Market = """{"market":{"ticker":"KXA-1","event_ticker":"EV-1","market_type":"binary","status":"active","exchange_index":0}}""";
    private const string Event = """{"event":{"event_ticker":"EV-1","series_ticker":"S-1","title":"t","mutually_exclusive":false}}""";
    private const string Series = """{"series":{"ticker":"S-1","title":"t","frequency":"daily","category":"c","fee_type":"quadratic","fee_multiplier":1}}""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryKeyVault _vault = new();
    private readonly StubHttpMessageHandler _read = new();
    private readonly StubHttpMessageHandler _trade = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false, utcStart: AccountData.T0);   // manual: the balance poll must not spin through the scripted answers

    public TradingSessionsTests()
    {
        Directory.CreateDirectory(_folder);
        TradingKeyLoader.Store(_vault, "demo", "kid-demo", TestKeys.NewPem());
        TradingKeyLoader.Store(_vault, "production", "kid-prod", TestKeys.NewPem());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TradingSessionOptions Options(KalshiSettings? settings = null, string? risk = null, TimeProvider? time = null) => new()
    {
        Settings = settings ?? KalshiSettings.Default,
        Vault = _vault,
        RiskPath = risk ?? Path.Combine(_folder, "risk.json"),
        StatePath = Path.Combine(_folder, "state.json"),
        AuditPath = Path.Combine(_folder, "audit.jsonl"),
        ReadHandler = _read,
        TradeHandler = _trade,
        SocketFactory = (_, _) => Task.FromResult<IAccountSocket>(new FakeAccountSocket()),
        Time = time ?? _time,
    };

    private void ScriptStart(bool budget = true)
    {
        if (budget)
        {
            _read.Respond(HttpStatusCode.OK, Limits).Respond(HttpStatusCode.OK, Costs);
        }

        _read.Respond(HttpStatusCode.OK, Status).Respond(HttpStatusCode.OK, Balance).Respond(HttpStatusCode.OK, Positions).Respond(HttpStatusCode.OK, NoOrders);
    }

    private void ScriptPrepare() =>
        _read.Respond(HttpStatusCode.OK, Market).Respond(HttpStatusCode.OK, Event).Respond(HttpStatusCode.OK, Series).Respond(HttpStatusCode.OK, Status).Respond(HttpStatusCode.OK, Balance);

    private static TradeRequest Buy() => new("KXA-1", TradeAction.Buy, TradeOutcome.Yes, 200, 400_000);

    [Fact]
    public async Task AReadSessionNeedsOnlyTheReadsAndSignsWithTheTradingKeyWhenThereIsNoDataKey()
    {
        _read.Respond(HttpStatusCode.OK, Balance);

        await using var session = await TradingSessions.OpenAsync(TradingMode.Read, Options(), Ct);
        var balance = await session.Reads.BalanceAsync(Ct);

        Assert.Equal(10_349_000, balance.BalanceDollars.Micros);
        Assert.Null(session.Service);
        Assert.Null(session.Trading);
        Assert.Contains("KALSHI-ACCESS-KEY", _read.Requests[0].Headers.Keys);
        Assert.Empty(_trade.Requests);
    }

    [Fact]
    public async Task AReadSessionUsesTheDataKeyWhenOneIsConfigured()
    {
        var pemFile = Path.Combine(_folder, "data.pem");
        await File.WriteAllTextAsync(pemFile, TestKeys.NewPem(), Ct);
        _read.Respond(HttpStatusCode.OK, Balance);
        var emptyVault = new MemoryKeyVault();
        var options = new TradingSessionOptions
        {
            Settings = KalshiSettings.Create(keyId: "data-kid", privateKeyPath: pemFile),
            Vault = emptyVault,
            RiskPath = Path.Combine(_folder, "risk.json"),
            StatePath = Path.Combine(_folder, "state.json"),
            ReadHandler = _read,
        };

        await using var session = await TradingSessions.OpenAsync(TradingMode.Read, options, Ct);
        await session.Reads.BalanceAsync(Ct);

        Assert.Equal("data-kid", _read.Requests[0].Headers["KALSHI-ACCESS-KEY"]);
    }

    [Fact]
    public async Task AMissingTradingKeyIsAClearErrorNamingTheCommand()
    {
        var thrown = await Assert.ThrowsAsync<AuthException>(() => TradingSessions.OpenAsync(TradingMode.Trade, Options().WithVault(new MemoryKeyVault()), Ct));

        Assert.Contains("kterm trading-key set", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("demo", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreviewSessionFollowsTheAccountButCanSendNothing()
    {
        ScriptStart(budget: false);
        ScriptPrepare();

        await using var session = await TradingSessions.OpenAsync(TradingMode.Preview, Options(), Ct);
        var prepared = await session.Service!.PrepareAsync(Buy(), Ct);

        Assert.True(prepared.Verdict.Ok);
        Assert.IsType<PreviewTrading>(session.Trading);
        await Assert.ThrowsAsync<TradingRefusedException>(() => session.Trading!.CancelAllAsync(Ct));
        Assert.Empty(_trade.Requests);
        Assert.Equal(["status", "balance", "positions", "orders"], _read.Requests.Take(4).Select(r => r.Uri.AbsolutePath.Split('/')[^1]));
    }

    [Fact]
    public async Task ATradeSessionReadsTheBudgetFromTheAccountAndPlacesTheOrderAsPrepared()
    {
        ScriptStart();
        ScriptPrepare();
        _read.Respond(HttpStatusCode.OK, Status).Respond(HttpStatusCode.OK, Balance);   // the fresh check before sending
        _trade.Respond(HttpStatusCode.Created, """{"order_id":"o-9","client_order_id":"c","fill_count":"0.00","remaining_count":"2.00"}""");

        await using var session = await TradingSessions.OpenAsync(TradingMode.Trade, Options(), Ct);
        var prepared = await session.Service!.PrepareAsync(Buy(), Ct);
        var placement = await session.Service.ExecuteAsync(prepared, Ct);

        Assert.Equal("o-9", placement.Created.OrderId);
        Assert.Equal(["limits", "endpoint_costs"], _read.Requests.Take(2).Select(r => r.Uri.AbsolutePath.Split('/')[^1]));
        var sent = Assert.Single(_trade.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Contains("\"price\":\"0.4000\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"side\":\"bid\"", sent.Body, StringComparison.Ordinal);
        var audit = await File.ReadAllTextAsync(Path.Combine(_folder, "audit.jsonl"), Ct);
        Assert.Contains("POST /portfolio/events/orders", audit, StringComparison.Ordinal);
        Assert.Contains("risk.execute", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNATURE", audit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IfTheBudgetCannotBeReadTheSettingsBudgetAndDefaultCostsApply()
    {
        _read.Respond(HttpStatusCode.Forbidden, "{}");
        ScriptStart(budget: false);

        await using var session = await TradingSessions.OpenAsync(TradingMode.Trade, Options(), Ct);

        Assert.NotNull(session.Service);
        Assert.IsType<KalshiTradingClient>(session.Trading);
    }

    [Fact]
    public async Task ProductionIsRefusedForOrdersUnlessTheRiskFileEnablesIt()
    {
        var production = KalshiSettings.Create(KalshiEnvironment.Production);
        _read.Respond(HttpStatusCode.OK, Limits).Respond(HttpStatusCode.OK, Costs);

        await Assert.ThrowsAsync<TradingRefusedException>(() => TradingSessions.OpenAsync(TradingMode.Trade, Options(production), Ct));

        var risk = Path.Combine(_folder, "enabled.json");
        await File.WriteAllTextAsync(risk, """{"trading":{"production_enabled":true}}""", Ct);
        ScriptStart();
        await using var allowed = await TradingSessions.OpenAsync(TradingMode.Trade, Options(production, risk), Ct);
        Assert.True(allowed.Production);
        Assert.True(allowed.Policy.ProductionEnabled);
    }

    [Fact]
    public async Task GettingOutIsAllowedInProductionWithoutTheRiskFileAndNeverFollowsTheStream()
    {
        var production = KalshiSettings.Create(KalshiEnvironment.Production);
        _read.Respond(HttpStatusCode.OK, Limits).Respond(HttpStatusCode.OK, Costs);
        _trade.Respond(HttpStatusCode.NoContent, string.Empty);   // cancel-all
        _read.Respond(HttpStatusCode.OK, NoOrders).Respond(HttpStatusCode.OK, NoOrders);   // two empty looks

        await using var session = await TradingSessions.OpenAsync(TradingMode.Exit, Options(production, time: new AutoAdvanceTimeProvider(autoFire: true, utcStart: AccountData.T0)), Ct);
        var report = await session.Service!.KillAsync("test", Ct);

        Assert.True(report.Clean);
        Assert.True(session.State.Halted);
        Assert.Equal(HttpMethod.Delete, Assert.Single(_trade.Requests).Method);
        Assert.DoesNotContain(_read.Requests, r => r.Uri.AbsolutePath.EndsWith("/portfolio/positions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionThatFailsToOpenClosesWhatItOpened()
    {
        ScriptStart();
        var failing = Options().WithVault(_vault);
        var options = new TradingSessionOptions
        {
            Settings = failing.Settings,
            Vault = _vault,
            RiskPath = failing.RiskPath,
            StatePath = failing.StatePath,
            AuditPath = failing.AuditPath,
            ReadHandler = _read,
            TradeHandler = _trade,
            SocketFactory = (_, _) => throw new HttpRequestException("socket down"),
            Time = _time,
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => TradingSessions.OpenAsync(TradingMode.Trade, options, Ct));
    }

    [Fact]
    public async Task TheDefaultAuditFileSitsNextToTheRiskFile()
    {
        await Task.CompletedTask;

        Assert.Equal(Path.GetDirectoryName(KalTerm.Trading.Risk.RiskPolicyLoader.DefaultPath()), Path.GetDirectoryName(TradingSessions.DefaultAuditPath()));
        Assert.EndsWith("audit.jsonl", TradingSessions.DefaultAuditPath(), StringComparison.Ordinal);
    }
}

internal static class OptionsExtensions
{
    /// <summary>The same options with another vault.</summary>
    public static TradingSessionOptions WithVault(this TradingSessionOptions options, ITradingKeyVault vault) => new()
    {
        Settings = options.Settings,
        Vault = vault,
        RiskPath = options.RiskPath,
        StatePath = options.StatePath,
        AuditPath = options.AuditPath,
        ReadHandler = options.ReadHandler,
        TradeHandler = options.TradeHandler,
        SocketFactory = options.SocketFactory,
        Time = options.Time,
    };
}
