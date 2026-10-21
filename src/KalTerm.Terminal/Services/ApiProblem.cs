using System.Net;
using KalTerm.Client;

namespace KalTerm.Terminal.Services;

/// <summary>Says in a sentence why a request to the server failed, for the failures every page treats alike.</summary>
internal static class ApiProblem
{
    /// <summary>The failures a request can end in that the person is told about (anything else is a bug and is not caught).</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> for an API refusal, an unreachable server or a certificate problem.</returns>
    public static bool IsExpected(Exception ex) => ex is ApiException or ApiUnavailableException or TlsTrustException;

    /// <summary>A sentence for a failure.</summary>
    /// <param name="ex">The exception.</param>
    /// <param name="doing">What was being done, as a verb phrase: "read your watchlists".</param>
    /// <returns>The sentence.</returns>
    public static string Say(Exception ex, string doing) =>
        ex is ApiException { Status: HttpStatusCode.Unauthorized } or ApiException { Code: "unauthorized" }
            ? "The server did not accept the token."
            : $"Could not {doing}: {ex.Message}";
}
