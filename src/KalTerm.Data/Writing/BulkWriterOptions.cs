namespace KalTerm.Data.Writing;

/// <summary>Tuning for a <see cref="BulkWriter{T}"/>. The defaults are the M3 decisions (PLAN.md 6.4); the buffer is tuned on the target server in slice 3.8.</summary>
public sealed record BulkWriterOptions
{
    /// <summary>A batch is written once it holds this many rows.</summary>
    public int FlushRows { get; init; } = 5_000;

    /// <summary>A batch is written this long after its first row arrived, however few rows it holds.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Rows the buffer holds while the database is slow or down (about 30 s at 16,000 rows/s). When it is full new rows are dropped,
    /// counted, and their time range recorded so the caller can log a gap; the producer is never blocked.
    /// </summary>
    public int MaxBufferedRows { get; init; } = 500_000;

    /// <summary>The first retry delay after a retryable failure; it doubles up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest retry delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Up to this fraction of each retry delay is randomly added, so several writers do not retry in step.</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>The writer reports unhealthy after this many consecutive failed attempts.</summary>
    public int UnhealthyAfterFailures { get; init; } = 5;

    /// <summary>On shutdown the remaining rows are written for at most this long; what is left is dropped and counted.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Checks the values; called when a writer is created.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FlushRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBufferedRows, FlushRows);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FlushInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBackoff, InitialBackoff);
        ArgumentOutOfRangeException.ThrowIfNegative(JitterFraction);
        ArgumentOutOfRangeException.ThrowIfLessThan(UnhealthyAfterFailures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShutdownTimeout, TimeSpan.Zero);
    }
}
