namespace KalTerm.Kalshi.WebSocket;

/// <summary>An active subscription. Sids change when a connection is replaced, so the channel and parameters are what identify it.</summary>
/// <param name="Sid">Kalshi's subscription id on the current connection.</param>
/// <param name="Channel">The channel name, exactly as it was requested.</param>
/// <param name="MarketTickers">The markets subscribed to; empty when the channel is not per-market.</param>
/// <param name="ParametersJson">Every other request parameter (not <c>channels</c>) as a JSON object, to re-send on resubscribe.</param>
public sealed record WsSubscription(int Sid, string Channel, IReadOnlyList<string> MarketTickers, string ParametersJson);
