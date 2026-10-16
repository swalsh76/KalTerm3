namespace KalTerm.TestSupport;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> whose sleeps cost no real time: creating a timer moves the clock
/// forward by its delay and fires it at once. The same idea as the reference's FakeTime, whose sleep advances time.
/// </summary>
public sealed class AutoAdvanceTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];
    private readonly Queue<(TimeSpan Due, TimerCallback Callback, object? State)> _pending = new();
    private readonly Lock _sync = new();
    private readonly bool _autoFire;
    private long _ticks;

    /// <param name="autoFire">
    /// True (the default): a timer fires at once. False: timers wait in a queue until the test calls
    /// <see cref="FireNext"/>, so a test can step waiters one at a time and observe their order.
    /// </param>
    public AutoAdvanceTimeProvider(bool autoFire = true)
    {
        _autoFire = autoFire;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers created but not yet fired (manual mode only).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Fires the oldest waiting timer: moves the clock forward by its delay, then runs it (manual mode).</summary>
    public void FireNext()
    {
        (TimeSpan Due, TimerCallback Callback, object? State) next;
        lock (_sync)
        {
            next = _pending.Dequeue();
            _ticks += next.Due.Ticks;
        }

        ThreadPool.QueueUserWorkItem(_ => next.Callback(next.State));
    }

    /// <summary>Time elapsed on this clock.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromTicks(_ticks);
            }
        }
    }

    /// <summary>Every wait that was requested, in order.</summary>
    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_sync)
            {
                return [.. _delays];
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _ticks;
        }
    }

    /// <summary>Moves the clock forward without any waiter asking for it (an idle period).</summary>
    public void Advance(TimeSpan by)
    {
        lock (_sync)
        {
            _ticks += by.Ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_sync)
            {
                _delays.Add(dueTime);
                if (!_autoFire)
                {
                    _pending.Enqueue((dueTime, callback, state));
                    return new InertTimer();
                }

                _ticks += dueTime.Ticks;
            }

            // Fire from the thread pool: the caller may not have finished constructing its wait yet.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
