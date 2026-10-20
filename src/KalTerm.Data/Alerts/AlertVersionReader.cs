using System.Globalization;
using Dapper;

namespace KalTerm.Data.Alerts;

/// <summary>Reads the counter that goes up whenever an alert fires, so a listener knows when to look for new fires.</summary>
public interface IAlertVersionReader
{
    /// <summary>The counter; 0 until the first fire.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The counter.</returns>
    Task<long> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAlertVersionReader"/> over the <c>analyzer.alerts.version</c> row of <c>dbo.ingest_state</c>, which the analyzer runner raises when the alerts analyzer reports a fire.</summary>
/// <param name="source">Opens connections.</param>
public sealed class AlertVersionReader(DataSource source) : IAlertVersionReader
{
    /// <summary>The state row the runner raises.</summary>
    public const string StateName = "analyzer.alerts.version";

    /// <inheritdoc/>
    public async Task<long> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var value = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM dbo.ingest_state WHERE name = @name",
            new { name = new DbString { Value = StateName, IsAnsi = true, Length = 64 } },
            commandTimeout: 15,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }
}
