using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Writing;

/// <summary>Decides whether a failed write is worth retrying.</summary>
public static class SqlRetryPolicy
{
    /// <summary>Deadlock victim (BUILD-LOG 2.4).</summary>
    public const int DeadlockVictim = 1205;

    /// <summary>The partition-switch/rebuild lock conflict seen in S-2 (BUILD-LOG 2.4).</summary>
    public const int PartitionLockConflict = 4891;

    /// <summary>The client's command timeout.</summary>
    public const int Timeout = -2;

    /// <summary>Errors seen while the server restarts or recovers a database: cannot open the database (4060), being recovered (922), cannot be opened (945), login failed (18456).</summary>
    public static IReadOnlyList<int> RestartErrors { get; } = [4060, 922, 945, 18456];

    /// <summary>
    /// True for the failures a later attempt can fix: deadlock (1205), 4891, a timeout, anything SqlClient calls transient, connection-level
    /// failures (severity 20 and above, or no connection at all) and the errors of a server that is restarting or recovering a database
    /// (<see cref="RestartErrors"/>). A wrong database name therefore retries forever, visibly (the writer turns unhealthy and logs each
    /// attempt) rather than dropping data silently. Everything else
    /// (a refused value, a missing table) is a defect in the data or the schema and is not retried.
    /// </summary>
    /// <param name="exception">What the write threw.</param>
    /// <returns>Whether to retry.</returns>
    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is SqlException sql)
        {
            return sql.IsTransient || sql.Errors.Cast<SqlError>().Any(e => IsRetryable(e.Number, e.Class));
        }

        return exception is TimeoutException or IOException;
    }

    /// <summary>The same decision for one SQL error.</summary>
    /// <param name="number">The error number.</param>
    /// <param name="severity">The error's severity class.</param>
    /// <returns>Whether to retry.</returns>
    public static bool IsRetryable(int number, int severity) =>
        number is DeadlockVictim or PartitionLockConflict or Timeout
        || number is -1 or 2 or 53 // no connection / server not found
        || RestartErrors.Contains(number)
        || severity >= 20;
}
