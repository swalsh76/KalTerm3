using KalTerm.Terminal.Trading;

namespace KalTerm.Terminal.Tests;

internal static class TradingSamples
{
    public static readonly LimitsView Limits = new(1_000_000, 3_000_000, 2_000_000, 3, 500, true);

    public static OrderView Order(string id = "ord-1", string ticker = "KX-A", string side = "bid", long price = 400_000, long initial = 200, long filled = 0, long remaining = 200, int shard = 0) =>
        new(id, ticker, shard, side, price, initial, filled, remaining, "resting");

    public static AccountView Account(bool production = true, bool synced = true, string fault = "", bool halted = false, string haltReason = "", IReadOnlyList<ShardView>? shards = null, long exposure = 500_000, long realized = 0, IReadOnlyList<OrderView>? orders = null, IReadOnlyList<PositionView>? positions = null, IReadOnlyList<FillView>? fills = null) =>
        new(production, synced, fault, halted, haltReason, shards ?? [new ShardView(0, 1_200_000, true), new ShardView(1, 300_000, true)], exposure, realized, 0, orders ?? [], positions ?? [], fills ?? [], Limits);

    public static OrderPlan Plan(bool ok = true, bool amend = false, object? handle = null) =>
        new(ok ? ["BUY 2 YES KX-A at $0.4000", "worst-case cost $0.8100 (includes up to $0.0100 fee)"] : ["BUY 2 YES KX-A at $0.4000", "REFUSED: the order is bigger than the limit"], ok, amend, handle ?? new object());
}

/// <summary>A trading session a test controls: it answers from what the test set and remembers what it was asked. It can send nothing anywhere.</summary>
internal sealed class FakeTradingLink : ITradingLink
{
    public AccountView Account { get; set; } = TradingSamples.Account();

    public event Action? Changed;

    public bool Disposed { get; private set; }

    public List<OrderEntry> Prepared { get; } = [];

    public List<(string OrderId, long? Price, long? Count)> AmendPrepared { get; } = [];

    public List<OrderPlan> Sent { get; } = [];

    public List<string> Cancelled { get; } = [];

    public int Kills { get; private set; }

    public int Resumes { get; private set; }

    public OrderPlan NextPlan { get; set; } = TradingSamples.Plan();

    public Exception? PrepareFails { get; set; }

    public Exception? SendFails { get; set; }

    public Exception? CancelFails { get; set; }

    public IReadOnlyList<string> SendLines { get; set; } = ["order ord-9 placed (client id c-1); 0 filled, 2 resting"];

    public string CancelLine { get; set; } = "order ord-1 cancelled";

    public KillResult KillAnswer { get; set; } = new(2, [], []);

    /// <summary>What the person must type, in production.</summary>
    public string Phrase { get; set; } = "KX-A buy yes 2 0.40";

    public void RaiseChanged() => Changed?.Invoke();

    public Task<OrderPlan> PrepareAsync(OrderEntry entry, CancellationToken cancellationToken)
    {
        Prepared.Add(entry);
        return PrepareFails is null ? Task.FromResult(NextPlan) : Task.FromException<OrderPlan>(PrepareFails);
    }

    public Task<OrderPlan> PrepareAmendAsync(string orderId, long? priceE6, long? countE2, CancellationToken cancellationToken)
    {
        AmendPrepared.Add((orderId, priceE6, countE2));
        return PrepareFails is null ? Task.FromResult(NextPlan with { IsAmend = true }) : Task.FromException<OrderPlan>(PrepareFails);
    }

    public bool PhraseMatches(OrderPlan plan, string typed) => string.Equals(typed.Trim(), Phrase, StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> SendAsync(OrderPlan plan, CancellationToken cancellationToken)
    {
        Sent.Add(plan);
        return SendFails is null ? Task.FromResult(SendLines) : Task.FromException<IReadOnlyList<string>>(SendFails);
    }

    public Task<string> CancelAsync(string orderId, CancellationToken cancellationToken)
    {
        Cancelled.Add(orderId);
        return CancelFails is null ? Task.FromResult(CancelLine) : Task.FromException<string>(CancelFails);
    }

    public Task<KillResult> KillAsync(CancellationToken cancellationToken)
    {
        Kills++;
        return Task.FromResult(KillAnswer);
    }

    public void Resume()
    {
        Resumes++;
        Account = Account with { Halted = false, HaltReason = string.Empty };
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens the fake session, or fails, or waits, as a test says.</summary>
internal sealed class FakeTradingGateway : ITradingGateway
{
    public FakeTradingLink Link { get; set; } = new();

    public Exception? OpenFails { get; set; }

    /// <summary>When set, opening waits for this (or for the limit's cancellation).</summary>
    public TaskCompletionSource? OpenHold { get; set; }

    public int Opens { get; private set; }

    public int Kills { get; private set; }

    public KillResult KillAnswer { get; set; } = new(1, [], []);

    public async Task<ITradingLink> OpenAsync(CancellationToken cancellationToken)
    {
        Opens++;
        if (OpenHold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return OpenFails is null ? Link : throw OpenFails;
    }

    public Task<KillResult> KillAsync(CancellationToken cancellationToken)
    {
        Kills++;
        return Task.FromResult(KillAnswer);
    }
}
