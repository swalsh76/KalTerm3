using KalTerm.Data.Writing;

namespace KalTerm.Data.Tests.Writing;

internal sealed class FakeSink : IBatchSink<TickerRow>, IDisposable
{
    private readonly Lock _sync = new();
    private readonly List<IReadOnlyList<TickerRow>> _batches = [];
    private int _attempts;

    /// <summary>Thrown by the next writes, one per attempt, before writes start succeeding.</summary>
    public Queue<Exception> Failures { get; } = new();

    /// <summary>When set, every write throws this.</summary>
    public Exception? AlwaysFail { get; set; }

    /// <summary>When set, a write waits for it (or for cancellation) before completing.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public bool Disposed { get; private set; }

    public int Attempts => Volatile.Read(ref _attempts);

    public IReadOnlyList<IReadOnlyList<TickerRow>> Batches
    {
        get
        {
            lock (_sync)
            {
                return [.. _batches];
            }
        }
    }

    public async Task WriteAsync(IReadOnlyList<TickerRow> batch, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        if (AlwaysFail is { } always)
        {
            throw always;
        }

        lock (_sync)
        {
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        lock (_sync)
        {
            _batches.Add([.. batch]);
        }
    }

    public void Dispose() => Disposed = true;
}

internal static class WriterTestKit
{
    public static readonly DateTime Epoch = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TickerRow Row(int i) => new(Epoch.AddSeconds(i), i, PriceE6: i);

    /// <summary>Waits (real time, bounded), firing the fake clock's waiting timers as it goes so flush windows and backoffs elapse.</summary>
    public static async Task PumpUntilAsync(AutoAdvanceTimeProvider time, Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (time.PendingTimers > 0)
            {
                time.FireNext();
            }

            await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>Waits (real time, bounded) without firing any timer.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
