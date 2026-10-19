using System.Globalization;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data;

/// <summary>
/// Maintains the daily (UTC) partitions shared by the <c>ticker</c> and <c>trade</c> tables: adds day boundaries ahead of need
/// and drops a whole day with <c>TRUNCATE ... WITH (PARTITIONS (n))</c>, never <c>DELETE</c> (BUILD-LOG 2.4).
/// </summary>
public static class PartitionManager
{
    private static readonly string[] TimeSeriesTables = ["dbo.ticker", "dbo.trade"];

    /// <summary>The existing day boundaries, ascending.</summary>
    /// <param name="connection">An open connection to the KalTerm database.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The days that have a boundary.</returns>
    public static async Task<IReadOnlyList<DateOnly>> GetBoundariesAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        const string sql = """
            SELECT CAST(v.value AS datetime2(3))
            FROM sys.partition_functions f
            JOIN sys.partition_range_values v ON v.function_id = f.function_id
            WHERE f.name = N'pf_day'
            ORDER BY v.boundary_id
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var days = new List<DateOnly>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            days.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return days;
    }

    /// <summary>
    /// Adds a boundary for each day from <paramref name="from"/> to <paramref name="to"/> (inclusive) that has none.
    /// Intended for days after the newest boundary, where the split moves no data; a missing day inside populated history is
    /// added too but is then an expensive split, so call this with <c>from</c> = today.
    /// </summary>
    /// <param name="connection">An open connection to the KalTerm database.</param>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The days whose boundary was added.</returns>
    public static async Task<IReadOnlyList<DateOnly>> EnsureBoundariesAsync(SqlConnection connection, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);

        var existing = (await GetBoundariesAsync(connection, cancellationToken).ConfigureAwait(false)).ToHashSet();
        var added = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (existing.Contains(day))
            {
                continue;
            }

            var literal = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var sql = $"ALTER PARTITION SCHEME ps_day NEXT USED [PRIMARY]; ALTER PARTITION FUNCTION pf_day() SPLIT RANGE ('{literal}')";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            added.Add(day);
        }

        return added;
    }

    /// <summary>Empties the partition of one day in every time-series table. Refuses a day that has no boundary of its own.</summary>
    /// <param name="connection">An open connection to the KalTerm database.</param>
    /// <param name="day">The UTC day to drop.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="InvalidOperationException">The day has no boundary, so its partition holds other days too.</exception>
    public static async Task TruncateDayAsync(SqlConnection connection, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var boundaries = await GetBoundariesAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!boundaries.Contains(day))
        {
            throw new InvalidOperationException($"No partition boundary for {day:yyyy-MM-dd}; refusing to truncate a partition that holds other days.");
        }

        // Partition numbers are 1-based and partition 1 is everything before the first boundary.
        var number = boundaries.ToList().IndexOf(day) + 2;
        foreach (var table in TimeSeriesTables)
        {
            var sql = string.Format(CultureInfo.InvariantCulture, "TRUNCATE TABLE {0} WITH (PARTITIONS ({1}))", table, number);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
