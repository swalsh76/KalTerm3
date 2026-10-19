using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace KalTerm.Data;

/// <summary>Forwards DbUp's log to an <see cref="ILogger"/> (or nowhere, when there is none).</summary>
internal sealed partial class LoggerUpgradeLog(ILogger? logger) : IUpgradeLog
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
