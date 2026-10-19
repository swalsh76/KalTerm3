namespace KalTerm.Data;

/// <summary>Keeps day partitions ahead of the clock so rows never land in the last partition (which can no longer be split once it holds rows).</summary>
public interface IPartitionMaintenance
{
    /// <summary>Adds a boundary for every day from <paramref name="today"/> through <paramref name="daysAhead"/> days later that has none.</summary>
    /// <param name="today">The current UTC day.</param>
    /// <param name="daysAhead">How many days past today to cover, at least 1.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The days whose boundary was added.</returns>
    Task<IReadOnlyList<DateOnly>> EnsureAheadAsync(DateOnly today, int daysAhead, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IPartitionMaintenance"/> over the database's partition function.</summary>
/// <param name="source">Opens connections.</param>
public sealed class PartitionMaintenance(DataSource source) : IPartitionMaintenance
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<DateOnly>> EnsureAheadAsync(DateOnly today, int daysAhead, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(daysAhead, 1);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await PartitionManager.EnsureBoundariesAsync(connection, today, today.AddDays(daysAhead), cancellationToken).ConfigureAwait(false);
    }
}
