namespace KalTerm.Kalshi.WebSocket;

/// <summary>Queue health of a <see cref="KalshiWebSocket"/>, for monitoring.</summary>
/// <param name="QueueDepth">Messages waiting for the consumer now.</param>
/// <param name="QueueLimit">The most that may wait before the client reconnects to bound memory.</param>
/// <param name="HighWater">The deepest the queue has been.</param>
/// <param name="Overflows">How many times the queue overflowed.</param>
/// <param name="Dropped">Messages received but discarded, in total.</param>
public sealed record WsStats(int QueueDepth, int QueueLimit, int HighWater, long Overflows, long Dropped);
