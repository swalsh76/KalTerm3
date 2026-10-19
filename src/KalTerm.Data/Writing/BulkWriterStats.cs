namespace KalTerm.Data.Writing;

/// <summary>A snapshot of a <see cref="BulkWriter{T}"/>.</summary>
/// <param name="Buffered">Rows waiting in the buffer (not counting a batch being written).</param>
/// <param name="Written">Rows committed to the database.</param>
/// <param name="Dropped">Rows lost: buffer full, a batch the database refused, or left over at shutdown.</param>
/// <param name="Retries">Retry attempts made after retryable failures.</param>
/// <param name="PoisonBatches">Batches dropped because the database refused them for a reason a retry cannot fix.</param>
/// <param name="ConsecutiveFailures">Failed attempts in a row since the last success.</param>
/// <param name="Healthy">False once <see cref="ConsecutiveFailures"/> reaches the limit; true again after a success.</param>
/// <param name="LastSuccess">When a batch last committed, or <see langword="null"/> if none has.</param>
/// <param name="LastError">The most recent failure message, or <see langword="null"/> if none.</param>
/// <param name="LastWrite">How long the last successful batch took to commit.</param>
/// <param name="MaxWrite">The longest a successful batch has taken to commit.</param>
public sealed record BulkWriterStats(
    int Buffered,
    long Written,
    long Dropped,
    long Retries,
    long PoisonBatches,
    int ConsecutiveFailures,
    bool Healthy,
    DateTimeOffset? LastSuccess,
    string? LastError,
    TimeSpan LastWrite,
    TimeSpan MaxWrite);

/// <summary>The time span covered by rows a writer dropped, for the gap log.</summary>
/// <param name="From">The earliest dropped row's timestamp (UTC).</param>
/// <param name="To">The latest dropped row's timestamp (UTC).</param>
/// <param name="Rows">How many rows were dropped in the span.</param>
public sealed record DroppedRange(DateTime From, DateTime To, long Rows);
