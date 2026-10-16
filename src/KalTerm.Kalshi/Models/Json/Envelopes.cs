namespace KalTerm.Kalshi.Models.Json;

// Single-object endpoints wrap the object under its own name: {"market": {...}}. A missing wrapper is a response error.

/// <summary>The body of <c>GET /markets/{ticker}</c>.</summary>
/// <param name="Market">The market.</param>
internal sealed record MarketEnvelope(Market Market);

/// <summary>The body of <c>GET /events/{event_ticker}</c>.</summary>
/// <param name="Event">The event.</param>
internal sealed record EventEnvelope(KalshiEvent Event);

/// <summary>The body of <c>GET /series/{series_ticker}</c>.</summary>
/// <param name="Series">The series.</param>
internal sealed record SeriesEnvelope(Series Series);
