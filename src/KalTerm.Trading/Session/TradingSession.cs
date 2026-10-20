using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Session;

/// <summary>How much of the trading machinery a command needs.</summary>
public enum TradingMode
{
    /// <summary>Only account reads (balance, positions, orders, fills).</summary>
    Read,

    /// <summary>A dry run: everything is read and the account is followed, but the trading client refuses every write, so nothing can be sent.</summary>
    Preview,

    /// <summary>Getting out: the real trading client with the order service, but no account stream, so cancelling and the kill switch never wait on a WebSocket or a full account read.</summary>
    Exit,

    /// <summary>Everything: the account stream is started and fully read before the session is handed out, so the risk layer sees a synced snapshot.</summary>
    Trade,
}

/// <summary>The account reads the commands show.</summary>
public interface IAccountReads
{
    /// <summary>The balance with its per-shard rows.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The balance.</returns>
    Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken);

    /// <summary>Market positions.</summary>
    /// <param name="includeSettled">Whether settled and flat markets are included.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The positions.</returns>
    IAsyncEnumerable<MarketPosition> PositionsAsync(bool includeSettled, CancellationToken cancellationToken);

    /// <summary>Orders.</summary>
    /// <param name="status">resting, canceled or executed; <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The orders.</returns>
    IAsyncEnumerable<Order> OrdersAsync(string? status, CancellationToken cancellationToken);

    /// <summary>Fills.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The fills.</returns>
    IAsyncEnumerable<Fill> FillsAsync(CancellationToken cancellationToken);
}

/// <summary>The real REST client behind <see cref="IAccountReads"/>.</summary>
/// <param name="client">The signed read client.</param>
public sealed class KalshiAccountReads(KalshiRestClient client) : IAccountReads
{
    /// <inheritdoc />
    public Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken) => client.BalanceAsync(cancellationToken: cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<MarketPosition> PositionsAsync(bool includeSettled, CancellationToken cancellationToken) =>
        client.IteratePositionsAsync(includeSettled ? KalshiQuery.Empty.With("settlement_status", "all") : null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Order> OrdersAsync(string? status, CancellationToken cancellationToken) =>
        client.IterateOrdersAsync(status is null ? null : KalshiQuery.Empty.With("status", status), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Fill> FillsAsync(CancellationToken cancellationToken) => client.IterateFillsAsync(null, cancellationToken);
}

/// <summary>Stands in for the trading client in a dry run: any attempt to send raises, so a dry run cannot send anything.</summary>
public sealed class PreviewTrading : ITradingApi
{
    /// <inheritdoc />
    public Task<CreatedOrder> CreateOrderAsync(OrderRequest order, CancellationToken cancellationToken = default) => throw Refused();

    /// <inheritdoc />
    public Task<ChangedOrder> AmendOrderAsync(string orderId, string ticker, BookSide side, long priceE6, long countE2, int exchangeIndex, CancellationToken cancellationToken = default) => throw Refused();

    /// <inheritdoc />
    public Task<CancelResult> CancelOrderAsync(CancelTarget target, CancellationToken cancellationToken = default) => throw Refused();

    /// <inheritdoc />
    public Task<IReadOnlyList<CancelResult>> CancelOrdersAsync(IReadOnlyList<CancelTarget> targets, CancellationToken cancellationToken = default) => throw Refused();

    /// <inheritdoc />
    public Task CancelAllAsync(CancellationToken cancellationToken = default) => throw Refused();

    /// <inheritdoc />
    public Task<string> TransferAsync(int sourceShard, int destinationShard, long amountE6, string transferId, CancellationToken cancellationToken = default) => throw Refused();

    private static TradingRefusedException Refused() => new("dry run: nothing can be sent");
}

/// <summary>What a command works with. Dispose it to close the connections.</summary>
public sealed class TradingSession : IAsyncDisposable
{
    private readonly List<Func<ValueTask>> _cleanup = [];

    /// <summary>Creates a session; <see cref="OnDispose"/> registers what to close.</summary>
    /// <param name="settings">The environment and budgets.</param>
    /// <param name="policy">The risk limits.</param>
    /// <param name="state">The persistent trading state.</param>
    /// <param name="reads">Account reads.</param>
    /// <param name="markets">Market reads (for cancelling by id).</param>
    public TradingSession(KalshiSettings settings, RiskPolicy policy, TradingState state, IAccountReads reads, IMarketRest markets)
    {
        Settings = settings;
        Policy = policy;
        State = state;
        Reads = reads;
        Markets = markets;
    }

    /// <summary>The environment and budgets.</summary>
    public KalshiSettings Settings { get; }

    /// <summary>The risk limits.</summary>
    public RiskPolicy Policy { get; }

    /// <summary>The persistent trading state.</summary>
    public TradingState State { get; }

    /// <summary>Account reads.</summary>
    public IAccountReads Reads { get; }

    /// <summary>Market reads.</summary>
    public IMarketRest Markets { get; }

    /// <summary>The order service; <see langword="null"/> in <see cref="TradingMode.Read"/>.</summary>
    public OrderService? Service { get; set; }

    /// <summary>The writes; <see langword="null"/> in <see cref="TradingMode.Read"/> and <see cref="TradingMode.Preview"/>.</summary>
    public ITradingApi? Trading { get; set; }

    /// <summary>The audit log, when the session writes.</summary>
    public ITradingAudit? Audit { get; set; }

    /// <summary>Whether this is the production account.</summary>
    public bool Production => Settings.IsProduction;

    /// <summary>Registers something to close when the session is disposed (closed in reverse order).</summary>
    /// <param name="close">What to close.</param>
    public void OnDispose(Func<ValueTask> close) => _cleanup.Add(close);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            await _cleanup[i]().ConfigureAwait(false);
        }

        _cleanup.Clear();
    }
}
