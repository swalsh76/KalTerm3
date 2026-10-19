namespace KalTerm.Data.Writing;

/// <summary>The part of a <see cref="BulkWriter{T}"/> that producers use; lets the logic in front of a writer be tested without a database.</summary>
/// <typeparam name="T">The row type.</typeparam>
public interface IRowWriter<in T>
{
    /// <summary>Queues a row. Never blocks.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="false"/> when the row was dropped (and counted).</returns>
    bool TryWrite(T row);

    /// <summary>Returns the time range covered by rows dropped since the last call, and resets it.</summary>
    /// <returns>The range, or <see langword="null"/> when nothing was dropped.</returns>
    DroppedRange? TakeDroppedRange();
}
