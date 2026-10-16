namespace KalTerm.Kalshi.Models;

/// <summary>Status of one exchange shard.</summary>
/// <param name="ExchangeIndex">The shard's index.</param>
/// <param name="Description">A short description.</param>
/// <param name="ExchangeActive">Whether the shard is up.</param>
/// <param name="TradingActive">Whether the shard is trading.</param>
/// <param name="IntraExchangeTransfersActive">Absent means unknown, so treated as not allowed: funding a shard fails closed.</param>
public sealed record ExchangeIndexStatus(
    int ExchangeIndex,
    string Description,
    bool ExchangeActive,
    bool TradingActive,
    bool IntraExchangeTransfersActive = false);

/// <summary>Whether the exchange is open and trading, from <c>GET /exchange/status</c>.</summary>
/// <param name="ExchangeActive">Whether the exchange is up.</param>
/// <param name="TradingActive">Whether trading is open.</param>
/// <param name="ExchangeEstimatedResumeTime">When trading is expected to resume, if it is paused.</param>
/// <param name="ExchangeIndexStatuses">Per-shard status, when reported.</param>
public sealed record ExchangeStatus(
    bool ExchangeActive,
    bool TradingActive,
    DateTimeOffset? ExchangeEstimatedResumeTime = null,
    IReadOnlyList<ExchangeIndexStatus>? ExchangeIndexStatuses = null);
