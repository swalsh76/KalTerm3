using System.Net.WebSockets;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Transport;

namespace KalTerm.Terminal.Trading;

/// <summary>Says in a sentence why a trading request failed, as <c>kterm</c> does. An unexpected exception is a bug and is not turned into a sentence.</summary>
internal static class TradingProblems
{
    /// <summary>Whether a failure is one the person is told about.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> for the failures trading can end in.</returns>
    public static bool IsExpected(Exception ex) => ex is OrderRefusedException or PlanChangedException or TransferFailedException or TransferUnknownException or FundingShortException
        or OrderNotFoundException or WriteOutcomeUnknownException or TradingRefusedException or KalshiApiException or HttpRequestException or WebSocketException
        or AuthException or TradingStateException or KalshiConfigurationException or RiskConfigException or ArgumentException or NotSupportedException or PlatformNotSupportedException or TimeoutException;

    /// <summary>Whether the failure leaves it unknown whether an order or a transfer reached Kalshi (it must be looked for, not repeated).</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> when the outcome is unknown.</returns>
    public static bool IsUnknown(Exception ex) => ex is WriteOutcomeUnknownException or TransferUnknownException;

    /// <summary>A sentence for a failure.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns>The sentence.</returns>
    public static string Say(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            PlanChangedException => "The cost or funding changed while you were deciding. Check the order again.",
            TransferFailedException => $"{ex.Message}; the order was not placed.",
            WriteOutcomeUnknownException unknown => $"UNKNOWN: {unknown.Message}. Look for {unknown.Reference} in Orders and Fills before trying again; do not repeat the order.",
            TradingRefusedException => $"{ex.Message} (production orders need KALSHI_ENV=production and \"ProductionEnabled\": true in risk.json).",
            KalshiApiException api => $"Kalshi said {api.StatusCode}: {(api.Body.Length > 300 ? api.Body[..300] : api.Body)}",
            HttpRequestException http => $"Cannot reach Kalshi: {http.GetType().Name}.",
            WebSocketException socket => socket.Message.Contains("401", StringComparison.Ordinal) || socket.Message.Contains("403", StringComparison.Ordinal)
                ? "Kalshi did not accept the key when opening its private stream (HTTP 401 or 403). A key made on the real site works only for KALSHI_ENV=production."
                : $"Cannot open Kalshi's private stream: {socket.Message}",
            TimeoutException => "Kalshi did not answer in time.",
            _ => ex.Message,
        };
    }
}
