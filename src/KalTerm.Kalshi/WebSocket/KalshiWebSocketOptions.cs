using Microsoft.Extensions.Logging;

namespace KalTerm.Kalshi.WebSocket;

/// <summary>Optional settings for <see cref="KalshiWebSocket"/>. Every property has a sensible default.</summary>
public sealed class KalshiWebSocketOptions
{
    /// <summary>The clock for command timeouts and the receive stamp; <see cref="TimeProvider.System"/> when omitted.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the production banner and dropped-message notices; nothing is logged when omitted.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>How long a command waits for its reply. Default 10 seconds.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many received messages may wait for the consumer. When the consumer falls further behind than this the connection
    /// is closed and <see cref="KalshiWebSocket.Messages"/> ends with an error, which bounds memory. Default 500,000.
    /// </summary>
    public int MaxQueueMessages { get; init; } = 500_000;

    /// <summary>Largest single message the real socket accepts. Default 16 MiB (a big order book snapshot must fit).</summary>
    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Whether to replace a lost connection and restore the subscriptions (with a <c>reconnected</c> message in the stream).
    /// When false a lost link ends <see cref="KalshiWebSocket.Messages"/> with an error. Default true.
    /// </summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>The first reconnect wait; it doubles on each failed attempt up to <see cref="ReconnectMax"/>, with jitter. Default 0.5 seconds.</summary>
    public TimeSpan ReconnectBase { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>The longest reconnect wait before jitter. Default 30 seconds.</summary>
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many consecutive failed reconnect attempts to make before giving up; <see langword="null"/> (the default) never gives up.</summary>
    public int? MaxReconnectAttempts { get; init; }

    /// <summary>For tests: replaces the backoff wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>For tests: a sample in [0, 1) replacing the random jitter source.</summary>
    internal Func<double>? JitterSample { get; init; }

    /// <summary>For tests: replaces the real socket.</summary>
    internal IWsConnector? Connector { get; init; }

    /// <summary>For tests: replaces the URL from the settings.</summary>
    internal Uri? Url { get; init; }
}
