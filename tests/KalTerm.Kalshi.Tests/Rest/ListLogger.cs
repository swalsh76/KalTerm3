using Microsoft.Extensions.Logging;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>An <see cref="ILogger"/> that keeps what it was given, so tests can check what was logged.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _sync = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
