using Microsoft.Extensions.Logging;

namespace KalTerm.Data.Tests;

internal sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public List<LogLevel> Levels { get; } = [];

    public Exception? LastException { get; private set; }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
            LastException = exception;
        }
    }
}
