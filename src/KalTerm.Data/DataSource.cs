using Microsoft.Data.SqlClient;

namespace KalTerm.Data;

/// <summary>Opens connections to the KalTerm database. The one place that turns options into a connection.</summary>
/// <param name="options">The connection options.</param>
public sealed class DataSource(DatabaseConnectionOptions options)
{
    /// <summary>The options in use.</summary>
    public DatabaseConnectionOptions Options { get; } = options;

    /// <summary>Opens a connection to the KalTerm database.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>An open connection; the caller disposes it.</returns>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(Options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
