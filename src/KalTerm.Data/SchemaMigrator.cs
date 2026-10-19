using System.Globalization;
using DbUp;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace KalTerm.Data;

/// <summary>Applies the embedded, forward-only SQL migrations with DbUp. The database must already exist (see <see cref="DatabaseProvisioner"/>).</summary>
/// <param name="logger">Receives the migration log; optional.</param>
public sealed class SchemaMigrator(ILogger? logger = null)
{
    /// <summary>Applies every script that is not yet in the journal, each in its own transaction.</summary>
    /// <param name="connectionString">A connection string for the KalTerm database.</param>
    /// <returns>The names of the scripts applied by this call, in order (empty when the schema was current).</returns>
    /// <exception cref="SchemaMigrationException">A script failed; the failing script's changes are rolled back.</exception>
    public IReadOnlyList<string> Migrate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(SchemaMigrator).Assembly, name => name.EndsWith(".sql", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new LoggerUpgradeLog(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new SchemaMigrationException("The schema migration failed: " + result.Error?.Message, result.Error);
        }

        return [.. result.Scripts.Select(s => s.Name)];
    }
}

/// <summary>A schema migration failed.</summary>
public sealed class SchemaMigrationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchemaMigrationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public SchemaMigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SchemaMigrationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
