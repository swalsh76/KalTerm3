using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using KalTerm.Core.Fees;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Transport;

namespace KalTerm.TestSupport;

/// <summary>Kalshi's REST side, scripted: four shards, shard 2 not trading.</summary>
public sealed class FakeAccountRest : IAccountRest
{
    public List<MarketPosition> Positions { get; set; } = [];

    public List<Order> Orders { get; set; } = [];

    public AccountBalance Balance { get; set; } = AccountData.Balance("10.3490", "0.0000", "0.0000", "2.5000");

    public ExchangeStatus Status { get; set; } = AccountData.StatusOf(transfers: true);

    public int BalanceCalls { get; set; }

    public Action<int>? OnBalanceRead { get; set; }

    public int FailBalance { get; set; }

    public int FailPositions { get; set; }

    public Task<ExchangeStatus> ExchangeStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

    public Task<AccountBalance> BalanceAsync(CancellationToken cancellationToken)
    {
        BalanceCalls++;
        OnBalanceRead?.Invoke(BalanceCalls);
        if (FailBalance != 0)
        {
            FailBalance--;
            throw new HttpRequestException("network down");
        }

        return Task.FromResult(Balance);
    }

    public async IAsyncEnumerable<MarketPosition> PositionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (FailPositions != 0)
        {
            FailPositions--;
            throw new HttpRequestException("network down");
        }

        foreach (var position in Positions)
        {
            yield return position;
        }
    }

    public async IAsyncEnumerable<Order> RestingOrdersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var order in Orders.Where(o => o.Status == "resting"))
        {
            yield return order;
        }
    }
}

/// <summary>The private WebSocket, scripted: messages are pushed by the test; completing the stream is the socket closing for good.</summary>
public sealed class FakeAccountSocket : IAccountSocket
{
    private readonly Channel<WsMessage> _queue = Channel.CreateUnbounded<WsMessage>();

    public List<string> Subscribed { get; } = [];

    public Task SubscribeAsync(string channel, CancellationToken cancellationToken)
    {
        Subscribed.Add(channel);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken) => _queue.Reader.ReadAllAsync(cancellationToken);

    public void Push(string type, string? json = null) =>
        _queue.Writer.TryWrite(new WsMessage(type, Msg: json is null ? null : JsonDocument.Parse(json).RootElement.Clone()));

    public void Close() => _queue.Writer.Complete();
}

public static class AccountData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public static AccountBalance Balance(params string[] amounts)
    {
        var rows = amounts.Select((a, i) => new ShardBalance(i, Dollars.Parse(a))).ToList();
        var total = rows.Aggregate(Dollars.Zero, (sum, r) => sum + r.Balance);
        return new AccountBalance(total.Micros / 10_000, total, 0, null, rows);
    }

    public static ExchangeStatus StatusOf(bool? transfers)
    {
        var rows = Enumerable.Range(0, 4).Select(i => new ExchangeIndexStatus(i, "s" + i, true, i != 2, transfers ?? false)).ToList();
        return new ExchangeStatus(true, true, null, rows);
    }

    public static MarketPosition Position(string ticker, string count, string exposure = "0", string realized = "0") =>
        new(ticker, 0, Contracts.Parse(count), null, Dollars.Parse(exposure), Dollars.Parse(realized));

    public static Order Order(string id = "o-1", string ticker = "KXA-1", string book = "bid", string yesPrice = "0.4000", string remaining = "2.00", string status = "resting")
    {
        var yes = Dollars.Parse(yesPrice);
        return new Order(id, ticker, status, book == "bid" ? "buy" : "sell", "yes", yes, Dollars.One - yes, Contracts.Parse("2.00"), Contracts.Zero, Contracts.Parse(remaining), BookSide: book);
    }

    public static string UserOrder(string id = "o-1", string status = "resting", string remaining = "2.00", string filled = "0.00", long updated = 1000, string book = "bid", string price = "0.4000") =>
        $$"""
        {"order_id":"{{id}}","client_order_id":"c-1","ticker":"KXA-1","exchange_index":0,"status":"{{status}}","side":"yes","outcome_side":"yes","book_side":"{{book}}",
         "yes_price_dollars":"{{price}}","initial_count_fp":"2.00","fill_count_fp":"{{filled}}","remaining_count_fp":"{{remaining}}","last_updated_ts_ms":{{updated}}}
        """;
}

/// <summary>A clock that stands still: reads give the same moment, waits are recorded and end at once. The service's time in these tests, like the reference's fixed NOW.</summary>
public sealed class InstantTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_delays)
            {
                return [.. _delays];
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => AccountData.T0;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_delays)
            {
                _delays.Add(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new Inert();
    }

    private sealed class Inert : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Kalshi's market side, scripted: two markets (shard 0 and shard 3), a quadratic fee series.</summary>
public sealed class FakeMarketRest(FakeAccountRest rest) : IMarketRest
{
    public Dictionary<string, Market> Markets { get; } = new()
    {
        ["KXA-1"] = MarketOf("KXA-1", 0),
        ["KXT-1"] = MarketOf("KXT-1", 3),
    };

    public string? FeeType { get; set; } = "quadratic";

    public FeeMultiplier? FeeMultiplier { get; set; } = KalTerm.Core.Fees.FeeMultiplier.One;

    public int SeriesCalls { get; private set; }

    public Func<IAsyncEnumerable<Order>>? Listing { get; set; }

    public static Market MarketOf(string ticker, int? shard, string status = "active") => new(ticker, "EV-" + ticker, "binary", status, ExchangeIndex: shard);

    public Task<Market> MarketAsync(string ticker, CancellationToken cancellationToken) => Task.FromResult(Markets[ticker]);

    public Task<KalshiEvent> EventAsync(string eventTicker, CancellationToken cancellationToken) => Task.FromResult(new KalshiEvent(eventTicker, "KXSERIES", "t", false));

    public Task<Series> SeriesAsync(string seriesTicker, CancellationToken cancellationToken)
    {
        SeriesCalls++;
        return Task.FromResult(new Series(seriesTicker, "t", "daily", "c", null, FeeType, FeeMultiplier));
    }

    public Task<Order> OrderAsync(string orderId, CancellationToken cancellationToken) =>
        Task.FromResult(rest.Orders.FirstOrDefault(o => o.OrderId == orderId) ?? throw new KalshiApiException(404, "not found"));

    public IAsyncEnumerable<Order> RestingOrdersAsync(CancellationToken cancellationToken) => Listing?.Invoke() ?? rest.RestingOrdersAsync(cancellationToken);
}

/// <summary>Records every write and moves money and orders in the fake exchange as Kalshi would.</summary>
public sealed class FakeTrading(FakeAccountRest rest, TradingState state) : ITradingApi
{
    public List<(string Name, object? Arg)> Calls { get; } = [];

    public Exception? TransferError { get; set; }

    public Exception? CreateError { get; set; }

    public Exception? AmendError { get; set; }

    public Exception? CancelAllError { get; set; }

    public Exception? CancelOrdersError { get; set; }

    public bool? HaltedWhenCancelAll { get; private set; }

    public List<Order> LateOrders { get; set; } = [];

    public bool CancelAllClears { get; set; } = true;

    public bool CancelNothing { get; set; }

    public Func<int, int, long, string, Task<string>>? TransferOverride { get; set; }

    public List<string> Names => [.. Calls.Select(c => c.Name)];

    public Task<string> TransferAsync(int sourceShard, int destinationShard, long amountE6, string transferId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("transfer", (sourceShard, destinationShard, amountE6, transferId)));
        if (TransferOverride is not null)
        {
            return TransferOverride(sourceShard, destinationShard, amountE6, transferId);
        }

        if (TransferError is not null)
        {
            throw TransferError;
        }

        var rows = rest.Balance.BalanceBreakdown!.ToDictionary(r => r.ExchangeIndex, r => r.Balance.Micros);
        rows[sourceShard] -= amountE6;
        rows[destinationShard] += amountE6;
        rest.Balance = AccountData.Balance([.. rows.OrderBy(r => r.Key).Select(r => Dollars.FromMicros(r.Value).ToString())]);
        return Task.FromResult("tr-ok");
    }

    public Task<CreatedOrder> CreateOrderAsync(OrderRequest order, CancellationToken cancellationToken = default)
    {
        Calls.Add(("create", order));
        return CreateError is not null ? throw CreateError : Task.FromResult(new CreatedOrder("o-new", order.ClientOrderId, 0, order.CountE2));
    }

    public Task<ChangedOrder> AmendOrderAsync(string orderId, string ticker, BookSide side, long priceE6, long countE2, int exchangeIndex, CancellationToken cancellationToken = default)
    {
        Calls.Add(("amend", (orderId, ticker, side, priceE6, countE2, exchangeIndex)));
        return AmendError is not null ? throw AmendError : Task.FromResult(new ChangedOrder(orderId, countE2));
    }

    public Task<CancelResult> CancelOrderAsync(CancelTarget target, CancellationToken cancellationToken = default)
    {
        Calls.Add(("cancel", target));
        return Task.FromResult(new CancelResult(target.OrderId, 100));
    }

    public Task<IReadOnlyList<CancelResult>> CancelOrdersAsync(IReadOnlyList<CancelTarget> targets, CancellationToken cancellationToken = default)
    {
        Calls.Add(("cancel_batch", targets.Select(t => t.OrderId).ToList()));
        if (CancelOrdersError is not null)
        {
            throw CancelOrdersError;
        }

        if (CancelNothing)
        {
            return Task.FromResult<IReadOnlyList<CancelResult>>([]);
        }

        var gone = targets.Select(t => t.OrderId).ToHashSet();
        rest.Orders = [.. rest.Orders.Where(o => !gone.Contains(o.OrderId))];
        return Task.FromResult<IReadOnlyList<CancelResult>>([.. targets.Select(t => new CancelResult(t.OrderId, null))]);
    }

    public Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        HaltedWhenCancelAll = state.Halted;
        Calls.Add(("cancel_all", null));
        if (CancelAllError is not null)
        {
            throw CancelAllError;
        }

        if (CancelAllClears)
        {
            rest.Orders = [];
        }

        rest.Orders = [.. rest.Orders, .. LateOrders];
        LateOrders = [];
        return Task.CompletedTask;
    }
}
