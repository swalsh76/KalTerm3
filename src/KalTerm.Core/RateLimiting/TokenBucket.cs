namespace KalTerm.Core.RateLimiting;

/// <summary>
/// An async token bucket that mirrors Kalshi's rate-limit model: it starts full, refills continuously
/// at a fixed rate up to its capacity, and callers wait first-come, first-served for the tokens they need.
/// Exceeding Kalshi's budget returns a bare HTTP 429 with no Retry-After, so clients pace themselves.
/// </summary>
/// <remarks>
/// All arithmetic is exact integers: tokens are held as micro-tokens multiplied by the timestamp frequency, so
/// refill and wait times carry no rounding drift. Time comes only from the injected <see cref="TimeProvider"/>.
/// A wait is rounded up to a whole millisecond (the granularity of timers) so that it never ends early; the
/// surplus stays in the bucket, so the long-run rate is exact.
/// </remarks>
public sealed class TokenBucket : IDisposable
{
    /// <summary>Tokens a typical Kalshi request costs.</summary>
    public const decimal DefaultRequestCost = 10m;

    private const long MicroScale = 1_000_000;
    private static readonly long MaxWaitTicks = TimeSpan.FromDays(24).Ticks;   // Task.Delay accepts under ~49 days

    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);     // held across a wait, so waiters queue in arrival order
    private readonly UInt128 _frequency;                  // timestamp ticks per second
    private readonly UInt128 _rateMicro;                  // micro-tokens per second
    private readonly UInt128 _capacityScaled;             // capacity in micro-tokens x frequency
    private UInt128 _tokensScaled;
    private long _updated;

    /// <param name="tokensPerSecond">The refill rate. At most six decimal places.</param>
    /// <param name="capacity">The most tokens the bucket holds, and the largest single cost. At most six decimal places.</param>
    /// <param name="timeProvider">The clock and timers; <see cref="TimeProvider.System"/> when omitted.</param>
    public TokenBucket(decimal tokensPerSecond, decimal capacity, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _time = timeProvider ?? TimeProvider.System;
        _frequency = (UInt128)_time.TimestampFrequency;
        _rateMicro = (UInt128)ToMicro(tokensPerSecond, nameof(tokensPerSecond));
        _capacityScaled = (UInt128)ToMicro(capacity, nameof(capacity)) * _frequency;
        _tokensScaled = _capacityScaled;   // starts full
        _updated = _time.GetTimestamp();
        TokensPerSecond = tokensPerSecond;
        Capacity = capacity;
    }

    public decimal TokensPerSecond { get; }

    public decimal Capacity { get; }

    /// <summary>
    /// Waits until <paramref name="cost"/> tokens are available, then spends them. Waiters are served in the order
    /// they arrive. Throws <see cref="ArgumentOutOfRangeException"/> if the cost is not within (0, capacity], and
    /// <see cref="OperationCanceledException"/> if cancelled while waiting (nothing is spent then).
    /// </summary>
    public async ValueTask AcquireAsync(decimal cost = DefaultRequestCost, CancellationToken cancellationToken = default)
    {
        if (cost <= 0 || cost > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), cost, $"cost must be in (0, {Capacity}]");
        }

        var costScaled = (UInt128)ToMicro(cost, nameof(cost)) * _frequency;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Refill();
            while (_tokensScaled < costScaled)
            {
                await Task.Delay(WaitFor(costScaled - _tokensScaled), _time, cancellationToken).ConfigureAwait(false);
                Refill();
            }

            _tokensScaled -= costScaled;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = now - _updated;
        if (elapsed <= 0)
        {
            _updated = now;   // no time passed, or the clock was reset backwards: add nothing and carry on from here
            return;
        }

        // tokens gained = rate [micro-tokens/s] x elapsed [ticks] / frequency, kept scaled by the frequency.
        var refilled = _tokensScaled + ((UInt128)elapsed * _rateMicro);
        _tokensScaled = UInt128.Min(_capacityScaled, refilled);
        _updated = now;
    }

    /// <summary>
    /// How long until <paramref name="deficitScaled"/> has refilled, rounded up to a whole millisecond: Task.Delay
    /// truncates to milliseconds, so an unrounded wait could come up short, and a sub-millisecond one would be zero.
    /// </summary>
    private TimeSpan WaitFor(UInt128 deficitScaled)
    {
        var timestampTicks = CeilingDivide(deficitScaled, _rateMicro);
        var timerTicks = CeilingDivide(timestampTicks * (UInt128)TimeSpan.TicksPerSecond, _frequency);
        var wholeMilliseconds = CeilingDivide(timerTicks, (UInt128)TimeSpan.TicksPerMillisecond);
        var waitTicks = UInt128.Min(wholeMilliseconds * (UInt128)TimeSpan.TicksPerMillisecond, (UInt128)MaxWaitTicks);
        return TimeSpan.FromTicks((long)waitTicks);   // a longer wait than the maximum just loops
    }

    private static UInt128 CeilingDivide(UInt128 numerator, UInt128 denominator)
    {
        var (quotient, remainder) = UInt128.DivRem(numerator, denominator);
        return remainder == UInt128.Zero ? quotient : quotient + UInt128.One;
    }

    private static long ToMicro(decimal value, string name)
    {
        if (value > long.MaxValue / MicroScale)
        {
            throw new ArgumentOutOfRangeException(name, value, "too large");
        }

        var scaled = value * MicroScale;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException("must have at most six decimal places", name);
        }

        return (long)scaled;
    }
}
