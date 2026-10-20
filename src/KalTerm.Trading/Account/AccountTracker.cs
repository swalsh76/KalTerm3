using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Kalshi.WebSocket;
using KalTerm.Trading.Risk;

namespace KalTerm.Trading.Account;

/// <summary>The REST calls the tracker needs.</summary>
public interface IAccountRest
{
    /// <summary>Exchange and per-shard status.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The status.</returns>
    Task<ExchangeStatus> ExchangeStatusAsync(CancellationToken cancellationToken);

    /// <summary>The balance, with its per-shard rows.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The balance.</returns>
    Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken);

    /// <summary>Every position, settled ones included.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The positions.</returns>
    IAsyncEnumerable<MarketPosition> PositionsAsync(CancellationToken cancellationToken);

    /// <summary>Every resting order.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The orders.</returns>
    IAsyncEnumerable<Order> RestingOrdersAsync(CancellationToken cancellationToken);
}

/// <summary>The private WebSocket calls the tracker needs.</summary>
public interface IAccountSocket
{
    /// <summary>Subscribes to a private channel.</summary>
    /// <param name="channel">The channel name.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when subscribed.</returns>
    Task SubscribeAsync(string channel, CancellationToken cancellationToken);

    /// <summary>Everything the socket delivers, including the synthetic <c>reconnected</c> message.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    /// <returns>The messages.</returns>
    IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken);
}

/// <summary>The real REST client behind <see cref="IAccountRest"/>.</summary>
/// <param name="client">The signed read client.</param>
public sealed class KalshiAccountRest(KalshiRestClient client) : IAccountRest
{
    /// <inheritdoc />
    public Task<ExchangeStatus> ExchangeStatusAsync(CancellationToken cancellationToken) => client.ExchangeStatusAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken) => client.BalanceAsync(cancellationToken: cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<MarketPosition> PositionsAsync(CancellationToken cancellationToken) =>
        client.IteratePositionsAsync(KalshiQuery.Empty.With("settlement_status", "all"), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Order> RestingOrdersAsync(CancellationToken cancellationToken) =>
        client.IterateOrdersAsync(KalshiQuery.Empty.With("status", "resting"), cancellationToken);
}

/// <summary>The real WebSocket behind <see cref="IAccountSocket"/>; connect it first.</summary>
/// <param name="socket">The connected, signed socket.</param>
public sealed class KalshiAccountSocket(KalshiWebSocket socket) : IAccountSocket
{
    /// <inheritdoc />
    public async Task SubscribeAsync(string channel, CancellationToken cancellationToken) =>
        await socket.SubscribeAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken) => socket.Messages(cancellationToken);
}

/// <summary>An order as Kalshi's YES book holds it. Only the book side and the YES price matter for risk; Kalshi's action and outcome labels are not dependable (a buy of NO is labelled "sell, no, ask").</summary>
/// <param name="OrderId">Kalshi's id.</param>
/// <param name="ClientOrderId">Our id.</param>
/// <param name="Ticker">The market.</param>
/// <param name="ExchangeIndex">The shard.</param>
/// <param name="Status">resting, canceled, executed or unknown.</param>
/// <param name="BookSide">bid or ask on the YES book.</param>
/// <param name="YesPriceE6">The YES price in millionths.</param>
/// <param name="InitialE2">Contracts when placed, in hundredths.</param>
/// <param name="FillE2">Contracts filled.</param>
/// <param name="RemainingE2">Contracts still resting.</param>
/// <param name="UpdatedMs">When last changed, in epoch milliseconds; an older message never replaces a newer one.</param>
public sealed record TrackedOrder(
    string OrderId,
    string ClientOrderId,
    string Ticker,
    int ExchangeIndex,
    string Status,
    string BookSide,
    long YesPriceE6,
    long InitialE2,
    long FillE2,
    long RemainingE2,
    long UpdatedMs)
{
    /// <summary>Whether it is on the book.</summary>
    public bool IsResting => Status == "resting";

    /// <summary>Filling a bid moves the net position towards YES, filling an ask towards NO.</summary>
    public bool AddsYes => BookSide == "bid";

    /// <summary>On the YES book a bid buys YES and an ask sells YES.</summary>
    public TradeAction Action => AddsYes ? TradeAction.Buy : TradeAction.Sell;
}

/// <summary>What the account holds in one market.</summary>
/// <param name="PositionE2">The net YES position in hundredths (negative: long NO).</param>
/// <param name="ExposureE6">Money at risk in it.</param>
/// <param name="RealizedE6">Cumulative realized profit in it.</param>
public sealed record TrackedPosition(long PositionE2 = 0, long ExposureE6 = 0, long RealizedE6 = 0);

/// <summary>
/// A live view of the account for the risk layer. Kalshi's private channels (<c>fill</c>, <c>user_orders</c>, <c>market_positions</c>) send no snapshot and no sequence numbers, so a message can be missed without anyone
/// knowing. The tracker therefore never trusts the stream alone: it re-reads everything over REST after subscribing and after every reconnect, and until that finishes the snapshot says the stream is not OK (the risk layer then
/// refuses). Messages that arrive while it re-reads are applied afterwards, in order, and are idempotent: an order message is a full replacement applied only if not older than what is held, a fill counts once by its trade id,
/// a position message replaces the market's position. There is no balance channel, so the balance is polled (every few seconds, and at once after a fill) and the snapshot's age is the age of the last good balance: if polling
/// fails the snapshot goes stale and orders are refused. Everything is exact integers.
/// </summary>
public sealed class AccountTracker : IAsyncDisposable
{
    /// <summary>The private channels followed.</summary>
    public static readonly IReadOnlyList<string> Channels = ["fill", "user_orders", "market_positions"];

    private const int RecentFills = 500;

    private readonly IAccountRest _rest;
    private readonly IAccountSocket _socket;
    private readonly TradingState _state;
    private readonly TimeProvider _time;
    private readonly TimeSpan _balanceInterval;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, long> _balances = [];
    private readonly Dictionary<int, (bool Trading, bool Transfers)> _flags = [];
    private readonly Dictionary<string, TrackedPosition> _positions = [];
    private readonly Dictionary<string, TrackedOrder> _orders = [];
    private readonly Queue<FillMsg> _fills = new();
    private readonly HashSet<string> _seenFills = [];
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _balanceAt;
    private bool _synced;
    private bool _connected;
    private bool _everSynced;
    private TaskCompletionSource _dirty = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task[] _tasks = [];

    /// <param name="rest">The REST calls.</param>
    /// <param name="socket">The private WebSocket.</param>
    /// <param name="state">The persistent trading state.</param>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="balanceInterval">How often the balance is polled; 5 seconds when omitted.</param>
    public AccountTracker(IAccountRest rest, IAccountSocket socket, TradingState state, TimeProvider? time = null, TimeSpan? balanceInterval = null)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(state);
        _rest = rest;
        _socket = socket;
        _state = state;
        _time = time ?? TimeProvider.System;
        _balanceInterval = balanceInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Raised (never throwing into the tracker) whenever what the snapshot shows may have changed.</summary>
    public event Action? Changed;

    /// <summary>The persistent trading state the tracker rolls and reads.</summary>
    public TradingState State => _state;

    /// <summary>The recent fills, oldest first (at most 500).</summary>
    public IReadOnlyList<FillMsg> Fills
    {
        get
        {
            lock (_gate)
            {
                return [.. _fills];
            }
        }
    }

    /// <summary>Why the stream stopped, when it did; empty while it runs.</summary>
    public string Fault { get; private set; } = string.Empty;

    /// <summary>Whether everything has been read at least once and nothing has been lost since.</summary>
    public bool Synced
    {
        get
        {
            lock (_gate)
            {
                return _synced;
            }
        }
    }

    /// <summary>The tracked orders, resting and recently finished.</summary>
    public IReadOnlyList<TrackedOrder> Orders
    {
        get
        {
            lock (_gate)
            {
                return [.. _orders.Values];
            }
        }
    }

    /// <summary>The tracked positions by market.</summary>
    public IReadOnlyDictionary<string, TrackedPosition> Positions
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, TrackedPosition>(_positions);
            }
        }
    }

    /// <summary>Subscribes, reads everything, then follows the stream. Throws if it cannot.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the tracker is synced and following.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in Channels)
        {
            await _socket.SubscribeAsync(channel, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _connected = true;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        _tasks = [Task.Run(() => FollowStreamAsync(_stop.Token), CancellationToken.None), Task.Run(() => PollBalanceAsync(_stop.Token), CancellationToken.None)];
    }

    /// <summary>Stops following. The snapshot reports the stream as not OK afterwards.</summary>
    /// <returns>A task that completes when the background work has ended.</returns>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var task in _tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _tasks = [];
        lock (_gate)
        {
            _connected = false;
            _synced = false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>Re-reads balance, shard status, positions and resting orders. Synced only when all of it succeeded.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when everything is read.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _synced = false;
        }

        await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);
        var positions = new Dictionary<string, TrackedPosition>();
        await foreach (var position in _rest.PositionsAsync(cancellationToken).ConfigureAwait(false))
        {
            positions[position.Ticker] = new TrackedPosition(position.PositionFp.Hundredths, position.MarketExposureDollars?.Micros ?? 0, position.RealizedPnlDollars?.Micros ?? 0);
        }

        var resting = new Dictionary<string, TrackedOrder>();
        await foreach (var order in _rest.RestingOrdersAsync(cancellationToken).ConfigureAwait(false))
        {
            resting[order.OrderId] = FromRest(order);
        }

        lock (_gate)
        {
            _positions.Clear();
            foreach (var (ticker, position) in positions)
            {
                _positions[ticker] = position;
            }

            // keep finished orders already known; replace the resting set with Kalshi's list
            foreach (var key in _orders.Where(o => o.Value.IsResting).Select(o => o.Key).ToList())
            {
                _orders.Remove(key);
            }

            foreach (var (id, order) in resting)
            {
                _orders[id] = order;
            }

            _state.Roll(TradingState.UtcDay(_time.GetUtcNow()));
            _state.SetBaseline(RealizedTotalLocked());
            _synced = true;
            _everSynced = true;
            Fault = string.Empty;
        }

        RaiseChanged();
    }

    /// <summary>Re-reads the balance and shard status (cheap; used before every order).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when read.</returns>
    public async Task RefreshBalanceAsync(CancellationToken cancellationToken = default) => await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Applies one private-channel message. Idempotent; anything else is ignored.</summary>
    /// <param name="message">The message.</param>
    public void Apply(WsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message.Payload())
        {
            case UserOrderMsg order:
                ApplyOrder(order);
                break;
            case MarketPositionMsg position:
                ApplyPosition(position);
                break;
            case FillMsg fill:
                ApplyFill(fill);
                break;
            default:
                return;
        }

        RaiseChanged();
    }

    /// <summary>The state for the risk check, or <see langword="null"/> before the first full read.</summary>
    /// <param name="excludeOrder">One resting order to leave out, so an amend is checked as its replacement without the old order counted twice.</param>
    /// <returns>The snapshot.</returns>
    public AccountSnapshot? Snapshot(string? excludeOrder = null)
    {
        lock (_gate)
        {
            if (_balanceAt is null || !_everSynced)
            {
                return null;
            }

            var resting = _orders.Values.Where(o => o.IsResting && o.OrderId != excludeOrder).ToList();
            var restingYes = new Dictionary<string, long>();
            var restingNo = new Dictionary<string, long>();
            var reserved = new Dictionary<int, long>();
            long restingCost = 0;
            foreach (var order in resting.Where(o => o.RemainingE2 > 0))
            {
                var side = order.AddsYes ? restingYes : restingNo;
                side[order.Ticker] = side.GetValueOrDefault(order.Ticker) + order.RemainingE2;
                var position = _positions.GetValueOrDefault(order.Ticker)?.PositionE2 ?? 0;
                var price = Math.Clamp(order.YesPriceE6, 1, 999_999);
                var cost = RiskChecker.CollateralE6(new OrderIntent(order.Ticker, order.ExchangeIndex, order.Action, TradeOutcome.Yes, order.RemainingE2, price, null), position);
                restingCost += cost;
                reserved[order.ExchangeIndex] = reserved.GetValueOrDefault(order.ExchangeIndex) + cost;
            }

            var tickers = _positions.Keys.Concat(restingYes.Keys).Concat(restingNo.Keys).Distinct();
            var markets = tickers.ToDictionary(
                ticker => ticker,
                ticker => new MarketState(_positions.GetValueOrDefault(ticker)?.PositionE2 ?? 0, restingYes.GetValueOrDefault(ticker), restingNo.GetValueOrDefault(ticker)));
            var shards = new Dictionary<int, ShardState>();
            foreach (var (index, balance) in _balances)
            {
                var (trading, transfers) = _flags.GetValueOrDefault(index);
                // Kalshi's balance is gross cash (a resting order does not reduce it, measured live), so what is free to use is the balance less what resting orders may need.
                shards[index] = new ShardState(index, Math.Max(balance - reserved.GetValueOrDefault(index), 0), trading, transfers);
            }

            var exposure = _positions.Values.Sum(p => p.ExposureE6) + restingCost;
            return new AccountSnapshot(
                _balanceAt.Value,
                shards,
                markets,
                resting.Count,
                exposure,
                _state.RealizedToday(RealizedTotalLocked()),
                _state.TransferredE6,
                _connected && _synced,
                _state.Halted);
        }
    }

    private static TrackedOrder FromRest(Order order)
    {
        var updated = order.LastUpdateTime ?? order.CreatedTime;
        return new TrackedOrder(
            order.OrderId,
            order.ClientOrderId,
            order.Ticker,
            order.ExchangeIndex,
            order.Status,
            order.BookSide,
            order.YesPriceDollars.Micros,
            order.InitialCountFp.Hundredths,
            order.FillCountFp.Hundredths,
            order.RemainingCountFp.Hundredths,
            updated?.ToUnixTimeMilliseconds() ?? 0);
    }

    private long RealizedTotalLocked() => _positions.Values.Sum(p => p.RealizedE6);

    private async Task ReadBalanceAsync(CancellationToken cancellationToken)
    {
        var status = await _rest.ExchangeStatusAsync(cancellationToken).ConfigureAwait(false);
        var balance = await _rest.BalanceAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _flags.Clear();
            foreach (var row in status.ExchangeIndexStatuses ?? [])
            {
                _flags[row.ExchangeIndex] = (row.ExchangeActive && row.TradingActive, row.ExchangeActive && row.IntraExchangeTransfersActive);
            }

            _balances.Clear();
            foreach (var row in balance.BalanceBreakdown ?? [])
            {
                _balances[row.ExchangeIndex] = row.Balance.Micros;
            }

            _balanceAt = _time.GetUtcNow();
        }

        RaiseChanged();
    }

    private async Task FollowStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _socket.Messages(cancellationToken).ConfigureAwait(false))
            {
                if (message.Type == KalshiWebSocket.ReconnectedType)
                {
                    await ResyncAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Apply(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Fault = ex.Message;   // anything unreadable on the stream fails closed: the snapshot says the stream is not OK
        }
        finally
        {
            lock (_gate)
            {
                _connected = false;
                _synced = false;
            }

            RaiseChanged();
        }
    }

    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(0.5);
        while (true)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fault = "refresh after a reconnect failed: " + ex.Message;
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }
    }

    private async Task PollBalanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TaskCompletionSource signal;
                lock (_gate)
                {
                    signal = _dirty;
                }

                await Task.WhenAny(signal.Task, Task.Delay(_balanceInterval, _time, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (signal.Task.IsCompleted)
                    {
                        _dirty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                try
                {
                    await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fault = "balance poll failed: " + ex.Message;   // the snapshot goes stale on its own
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // stopped
        }
    }

    private void ApplyOrder(UserOrderMsg msg)
    {
        var incoming = new TrackedOrder(
            msg.OrderId,
            msg.ClientOrderId,
            msg.Ticker,
            msg.ExchangeIndex,
            msg.Status,
            msg.BookSide,
            msg.YesPriceDollars.Micros,
            msg.InitialCountFp.Hundredths,
            msg.FillCountFp.Hundredths,
            msg.RemainingCountFp.Hundredths,
            msg.LastUpdatedTsMs ?? 0);
        lock (_gate)
        {
            if (_orders.TryGetValue(incoming.OrderId, out var held) && incoming.UpdatedMs < held.UpdatedMs)
            {
                return;   // older than what is held
            }

            _orders[incoming.OrderId] = incoming;
            if (_orders.Count > 1000)
            {
                foreach (var key in _orders.Where(o => !o.Value.IsResting).Select(o => o.Key).SkipLast(500).ToList())
                {
                    _orders.Remove(key);
                }
            }
        }
    }

    private void ApplyPosition(MarketPositionMsg msg)
    {
        lock (_gate)
        {
            var held = _positions.GetValueOrDefault(msg.MarketTicker) ?? new TrackedPosition();
            _positions[msg.MarketTicker] = new TrackedPosition(
                msg.PositionFp.Hundredths,
                msg.PositionCostDollars?.Micros ?? held.ExposureE6,
                msg.RealizedPnlDollars?.Micros ?? held.RealizedE6);
        }
    }

    private void ApplyFill(FillMsg msg)
    {
        lock (_gate)
        {
            if (!_seenFills.Add(msg.TradeId))
            {
                return;
            }

            _fills.Enqueue(msg);
            while (_fills.Count > RecentFills)
            {
                _fills.Dequeue();
            }

            if (_seenFills.Count > 4 * RecentFills)
            {
                _seenFills.Clear();
                _seenFills.UnionWith(_fills.Select(f => f.TradeId));
            }

            _dirty.TrySetResult();   // a fill moves cash: read the balance now
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // a listener's trouble must never break the account view
        }
    }
}
