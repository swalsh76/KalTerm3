using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Trading.Account;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Session;
using KalTerm.Trading.Transport;

namespace KalTerm.Terminal.Trading;

/// <summary>The real trading gateway: opens sessions the way <c>kterm buy</c> does (the trading key in Credential Manager, the environment from <c>KALSHI_ENV</c>, the limits from <c>risk.json</c>).</summary>
internal sealed class TradingGateway : ITradingGateway
{
    /// <summary>How many of the latest fills are read when a session opens (later ones arrive on the live stream).</summary>
    public const int InitialFills = 50;

    public async Task<ITradingLink> OpenAsync(CancellationToken cancellationToken)
    {
        var session = await TradingSessions.OpenAsync(TradingMode.Trade, Options(), cancellationToken).ConfigureAwait(false);
        try
        {
            var fills = new List<FillView>();
            await foreach (var fill in session.Reads.FillsAsync(cancellationToken).ConfigureAwait(false))
            {
                fills.Add(TradingLink.ViewOf(fill));
                if (fills.Count >= InitialFills)
                {
                    break;
                }
            }

            return new TradingLink(session, fills);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<KillResult> KillAsync(CancellationToken cancellationToken)
    {
        await using var session = await TradingSessions.OpenAsync(TradingMode.Exit, Options(), cancellationToken).ConfigureAwait(false);
        return TradingLink.ResultOf(await session.Service!.KillAsync("kill switch (terminal, no session)", cancellationToken).ConfigureAwait(false));
    }

    private static TradingSessionOptions Options() => new() { Settings = KalshiSettings.FromEnvironment() };
}

/// <summary>
/// The real session behind <see cref="ITradingLink"/>: the account as the tracker holds it (the same picture the risk layer checks orders against) and the order service. Nothing here decides anything: every order goes through
/// <see cref="OrderService"/>, which asks the risk layer, and the production confirmation is checked by the order service's own <see cref="OrderService.TypedPhraseMatches"/>.
/// </summary>
internal sealed class TradingLink : ITradingLink
{
    private const int MaxFills = 100;

    private readonly TradingSession _session;
    private readonly List<FillView> _initialFills;

    public TradingLink(TradingSession session, IReadOnlyList<FillView> initialFills)
    {
        _session = session;
        _initialFills = [.. initialFills];
        Tracker.Changed += OnChanged;
    }

    public event Action? Changed;

    public AccountView Account
    {
        get
        {
            var snapshot = Tracker.Snapshot();
            var orders = Tracker.Orders.Where(o => o.IsResting).OrderBy(o => o.Ticker, StringComparer.Ordinal).ThenBy(o => o.YesPriceE6).Select(ViewOf).ToList();
            var positions = Tracker.Positions.Where(p => p.Value.PositionE2 != 0).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new PositionView(p.Key, p.Value.PositionE2, p.Value.ExposureE6, p.Value.RealizedE6)).ToList();
            var shards = snapshot is null ? [] : snapshot.Shards.Values.OrderBy(s => s.Index).Select(s => new ShardView(s.Index, s.BalanceE6, s.TradingActive)).ToList();
            return new AccountView(
                _session.Production,
                Tracker.Synced,
                Tracker.Fault,
                _session.State.Halted,
                _session.State.HaltReason,
                shards,
                snapshot?.ExposureE6 ?? 0,
                snapshot?.RealizedPnlTodayE6 ?? 0,
                snapshot?.TransferredTodayE6 ?? 0,
                orders,
                positions,
                MergedFills(),
                LimitsView.Of(_session.Policy));
        }
    }

    private AccountTracker Tracker => _session.Tracker!;

    private OrderService Service => _session.Service!;

    public async Task<OrderPlan> PrepareAsync(OrderEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var request = new TradeRequest(
            entry.Ticker,
            entry.Buy ? TradeAction.Buy : TradeAction.Sell,
            entry.Yes ? TradeOutcome.Yes : TradeOutcome.No,
            entry.CountE2,
            entry.PriceE6,
            entry.ImmediateOrCancel ? TimeInForce.ImmediateOrCancel : TimeInForce.GoodTillCanceled,
            entry.PostOnly);
        var prepared = await Service.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        return new OrderPlan(OrderService.Describe(prepared), prepared.Verdict.Ok, IsAmend: false, prepared);
    }

    public async Task<OrderPlan> PrepareAmendAsync(string orderId, long? priceE6, long? countE2, CancellationToken cancellationToken)
    {
        var prepared = await Service.PrepareAmendAsync(orderId, priceE6, countE2, cancellationToken).ConfigureAwait(false);
        return new OrderPlan(OrderService.Describe(prepared), prepared.Verdict.Ok, IsAmend: true, prepared);
    }

    public bool PhraseMatches(OrderPlan plan, string typed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return OrderService.TypedPhraseMatches(typed, ((Prepared)plan.Handle).Request);
    }

    public async Task<IReadOnlyList<string>> SendAsync(OrderPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var prepared = (Prepared)plan.Handle;
        var lines = new List<string>();
        if (plan.IsAmend)
        {
            var amendment = await Service.ExecuteAmendAsync(prepared, cancellationToken).ConfigureAwait(false);
            lines.AddRange(amendment.Transfers.Select(TransferLine));
            lines.Add($"order {amendment.OrderId} amended" + (amendment.Result.RemainingCountE2 is { } left ? $"; {RiskChecker.CountText(left)} resting" : string.Empty));
            return lines;
        }

        var placement = await Service.ExecuteAsync(prepared, cancellationToken).ConfigureAwait(false);
        lines.AddRange(placement.Transfers.Select(TransferLine));
        var created = placement.Created;
        lines.Add($"order {created.OrderId} placed (client id {placement.ClientOrderId}); {RiskChecker.CountText(created.FillCountE2)} filled, {RiskChecker.CountText(created.RemainingCountE2)} resting"
            + (created.Recovered ? " (found by its client id after an unclear answer)" : string.Empty));
        return lines;
    }

    public async Task<string> CancelAsync(string orderId, CancellationToken cancellationToken)
    {
        var result = await Service.CancelAsync(orderId, cancellationToken).ConfigureAwait(false);
        return result.Error is { Length: > 0 } error ? $"order {orderId} was not cancelled: {error}" : result.AlreadyGone ? $"order {orderId} was already gone" : $"order {orderId} cancelled";
    }

    public async Task<KillResult> KillAsync(CancellationToken cancellationToken) =>
        ResultOf(await Service.KillAsync("kill switch (terminal)", cancellationToken).ConfigureAwait(false));

    public void Resume()
    {
        _session.State.ClearHalt();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Tracker.Changed -= OnChanged;
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The kill switch's report in the terminal's own shape.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The result.</returns>
    internal static KillResult ResultOf(KillReport report) => new(report.Cancelled.Count, [.. report.Remaining], [.. report.Errors]);

    /// <summary>A fill read over REST.</summary>
    /// <param name="fill">The fill.</param>
    /// <returns>The view.</returns>
    internal static FillView ViewOf(Fill fill) => new(
        fill.TradeId.Length > 0 ? fill.TradeId : fill.FillId,
        fill.CreatedTime ?? (fill.Ts is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null),
        fill.Ticker.Length > 0 ? fill.Ticker : fill.MarketTicker,
        fill.Action,
        fill.OutcomeSide,
        fill.YesPriceDollars.Micros,
        fill.CountFp.Hundredths,
        fill.FeeCost.Micros,
        fill.OrderId);

    private static FillView ViewOf(FillMsg fill) => new(
        fill.TradeId,
        fill.TsMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
        fill.MarketTicker,
        fill.Action,
        fill.OutcomeSide,
        fill.YesPriceDollars.Micros,
        fill.CountFp.Hundredths,
        fill.FeeCost.Micros,
        fill.OrderId);

    private static OrderView ViewOf(TrackedOrder order) => new(order.OrderId, order.Ticker, order.ExchangeIndex, order.BookSide, order.YesPriceE6, order.InitialE2, order.FillE2, order.RemainingE2, order.Status);

    private static string TransferLine(Transfer transfer) => $"moved {RiskChecker.DollarsText(transfer.AmountE6)} from shard {transfer.Source} to shard {transfer.Destination} first";

    /// <summary>The fills read when the session opened and the ones the stream brought since, newest first, each once.</summary>
    private List<FillView> MergedFills() =>
        [.. Tracker.Fills.Select(ViewOf).Concat(_initialFills).GroupBy(f => f.Key).Select(g => g.First()).OrderByDescending(f => f.Time ?? DateTimeOffset.MinValue).Take(MaxFills)];

    private void OnChanged() => Changed?.Invoke();
}
