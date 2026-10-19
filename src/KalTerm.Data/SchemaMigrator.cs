using System.Globalization;
using DbUp;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace KalTerm.Data;

/// <summary>Applies the embedded, forward-only SQL migrations with DbUp. The database must already exist (see <see cref="DatabaseProvisioner"/>).</summary>
/// <param name="logger">Receives the migration log; optional.</param>
public sealed partial class SchemaMigrator(ILogger? logger = null)
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

    private sealed partial class LoggerUpgradeLog(ILogger? logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => Write(LogLevel.Trace, null, format, args);

        public void LogDebug(string format, params object[] args) => Write(LogLevel.Debug, null, format, args);

        public void LogInformation(string format, params object[] args) => Write(LogLevel.Information, null, format, args);

        public void LogWarning(string format, params object[] args) => Write(LogLevel.Warning, null, format, args);

        public void LogError(string format, params object[] args) => Write(LogLevel.Error, null, format, args);

        public void LogError(Exception ex, string format, params object[] args) => Write(LogLevel.Error, ex, format, args);

        private static string Format(string format, object[] args) => args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);

        [LoggerMessage(Level = LogLevel.Debug, Message = "{Text}")]
        private static partial void Debug(ILogger logger, string text);

        [LoggerMessage(Level = LogLevel.Information, Message = "{Text}")]
        private static partial void Information(ILogger logger, string text);

        [LoggerMessage(Level = LogLevel.Warning, Message = "{Text}")]
        private static partial void Warning(ILogger logger, string text);

        [LoggerMessage(Level = LogLevel.Error, Message = "{Text}")]
        private static partial void Error(ILogger logger, Exception? exception, string text);

        private void Write(LogLevel level, Exception? exception, string format, object[] args)
        {
            if (logger is null)
            {
                return;
            }

            var text = Format(format, args);
            switch (level)
            {
                case LogLevel.Error:
                    Error(logger, exception, text);
                    break;
                case LogLevel.Warning:
                    Warning(logger, text);
                    break;
                case LogLevel.Information:
                    Information(logger, text);
                    break;
                default:
                    Debug(logger, text);
                    break;
            }
        }
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
