using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.RateLimiting;
using KalTerm.Kalshi.Rest;
using KalTerm.Kalshi.WebSocket;
using KalTerm.Trading.Account;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Session;

/// <summary>Where the session finds things. Everything has a real default; tests replace the vault, the paths and the connections.</summary>
public sealed class TradingSessionOptions
{
    /// <summary>The environment and budgets.</summary>
    public required KalshiSettings Settings { get; init; }

    /// <summary>Where trading keys are kept; the Windows Credential Manager when omitted.</summary>
    public ITradingKeyVault? Vault { get; init; }

    /// <summary>The risk file; <see cref="RiskPolicyLoader.DefaultPath"/> when omitted.</summary>
    public string? RiskPath { get; init; }

    /// <summary>The trading state file; <see cref="TradingState.DefaultPath"/> when omitted.</summary>
    public string? StatePath { get; init; }

    /// <summary>The audit log; <c>audit.jsonl</c> next to the risk file when omitted.</summary>
    public string? AuditPath { get; init; }

    /// <summary>The handler of the read client; a standard one when omitted.</summary>
    public HttpMessageHandler? ReadHandler { get; init; }

    /// <summary>The handler of the trading client; a standard one when omitted.</summary>
    public HttpMessageHandler? TradeHandler { get; init; }

    /// <summary>Makes and connects the private WebSocket; the real one when omitted.</summary>
    public Func<KalshiSigner, CancellationToken, Task<IAccountSocket>>? SocketFactory { get; init; }

    /// <summary>The clock; <see cref="TimeProvider.System"/> when omitted.</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>Assembles the trading machinery for a mode. Trading talks to Kalshi directly and never to the KalTerm server, so nothing here needs a server token.</summary>
public static class TradingSessions
{
    /// <summary>The audit log's default place, next to the risk file.</summary>
    /// <returns>The path.</returns>
    public static string DefaultAuditPath() => Path.Combine(Path.GetDirectoryName(RiskPolicyLoader.DefaultPath())!, "audit.jsonl");

    /// <summary>Opens what a command needs for <paramref name="mode"/>.</summary>
    /// <param name="mode">How much of the machinery.</param>
    /// <param name="options">Where things are.</param>
    /// <param name="cancellationToken">Cancels the opening.</param>
    /// <returns>The session; dispose it.</returns>
    /// <exception cref="AuthException">No usable key.</exception>
    public static async Task<TradingSession> OpenAsync(TradingMode mode, TradingSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Settings;
        var time = options.Time ?? TimeProvider.System;
        var policy = RiskPolicyLoader.Load(options.RiskPath);
        var state = TradingState.Load(options.StatePath);
        var environment = settings.IsProduction ? "production" : "demo";
        var cleanup = new List<Func<ValueTask>>();
        try
        {
            KalshiSigner signer;
            TradingSigner? trading = null;
            if (mode == TradingMode.Read && settings.KeyId is not null && settings.PrivateKeyPath is not null)
            {
                signer = KalshiSigner.FromSettings(settings, time);   // the data key, if there is one, for plain reads
                cleanup.Add(() => { signer.Dispose(); return ValueTask.CompletedTask; });
            }
            else
            {
                trading = TradingKeyLoader.Load(options.Vault ?? new CredentialManagerKeyVault(), environment, time)
                    ?? throw new AuthException($"no trading key is stored for {environment}: store one with `kterm trading-key set` (or, for reads only, set KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH)");
                signer = trading.Signer;
                cleanup.Add(() => { trading.Dispose(); return ValueTask.CompletedTask; });
            }

            var rest = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer, TimeProvider = time }, options.ReadHandler);
            cleanup.Add(() => { rest.Dispose(); return ValueTask.CompletedTask; });
            var reads = new KalshiAccountReads(rest);
            var markets = new KalshiMarketRest(rest);
            var session = new TradingSession(settings, policy, state, reads, markets);
            if (mode == TradingMode.Read)
            {
                Attach(session, cleanup);
                return session;
            }

            ITradingApi api;
            if (mode == TradingMode.Preview)
            {
                api = new PreviewTrading();
            }
            else
            {
                var budget = await ReadBudgetAsync(rest, settings, cancellationToken).ConfigureAwait(false);
                var bucket = budget?.CreateBucket(time) ?? KalshiRateLimits.WriteBucket(settings, time);
                cleanup.Add(() => { bucket.Dispose(); return ValueTask.CompletedTask; });
                var audit = new JsonLinesAudit(options.AuditPath ?? DefaultAuditPath());
                var clientOptions = new TradingClientOptions
                {
                    Bucket = bucket,
                    TimeProvider = time,
                    Audit = audit,
                    AllowProduction = mode == TradingMode.Exit || policy.ProductionEnabled,   // getting out always may; sending orders needs the risk file's say-so
                    DefaultCost = budget?.DefaultCost ?? 10m,
                    CancelCost = budget?.CancelCost ?? 2m,
                };
                var client = new KalshiTradingClient(settings, trading!, clientOptions, options.TradeHandler);
                cleanup.Add(() => { client.Dispose(); return ValueTask.CompletedTask; });
                api = client;
                session.Audit = audit;
            }

            IAccountSocket socket;
            if (mode == TradingMode.Exit)
            {
                socket = new ClosedSocket();   // the kill switch never waits on a stream
            }
            else
            {
                socket = options.SocketFactory is { } make ? await make(signer, cancellationToken).ConfigureAwait(false) : await ConnectAsync(settings, signer, cleanup, time, cancellationToken).ConfigureAwait(false);
            }

            var tracker = new AccountTracker(new KalshiAccountRest(rest), socket, state, time);
            cleanup.Add(tracker.DisposeAsync);
            if (mode != TradingMode.Exit)
            {
                await tracker.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            var service = new OrderService(markets, api, tracker, policy, settings.IsProduction, time, audit: session.Audit);
            cleanup.Add(service.DisposeAsync);
            session.Trading = api;
            session.Service = service;
            Attach(session, cleanup);
            return session;
        }
        catch
        {
            for (var i = cleanup.Count - 1; i >= 0; i--)
            {
                await cleanup[i]().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static void Attach(TradingSession session, List<Func<ValueTask>> cleanup)
    {
        foreach (var close in cleanup)
        {
            session.OnDispose(close);
        }
    }

    /// <summary>The account's real budget, or <see langword="null"/> when Kalshi cannot be asked (the settings' tier budget and the default costs then apply: conservative for the basic tier).</summary>
    private static async Task<TradingBudget?> ReadBudgetAsync(KalshiRestClient rest, KalshiSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await TradingBudget.ReadAsync(rest, settings.RateLimitMargin, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KalshiApiException or HttpRequestException or KalTerm.Kalshi.KalshiResponseException)
        {
            return null;
        }
    }

    private static async Task<IAccountSocket> ConnectAsync(KalshiSettings settings, KalshiSigner signer, List<Func<ValueTask>> cleanup, TimeProvider time, CancellationToken cancellationToken)
    {
        var socket = new KalshiWebSocket(settings, signer, new KalshiWebSocketOptions { TimeProvider = time });
        cleanup.Add(socket.DisposeAsync);
        await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new KalshiAccountSocket(socket);
    }

    /// <summary>The socket of a session that never follows the account (<see cref="TradingMode.Exit"/>).</summary>
    private sealed class ClosedSocket : IAccountSocket
    {
        public Task SubscribeAsync(string channel, CancellationToken cancellationToken) => throw new InvalidOperationException("this session does not follow the account stream");

        public async IAsyncEnumerable<WsMessage> Messages([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
