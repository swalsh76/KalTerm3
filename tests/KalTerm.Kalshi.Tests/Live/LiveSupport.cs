using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.Tests.Live;

/// <summary>What the opt-in live tests share: the skip reason, the credentials, and the summary file.</summary>
internal static class LiveSupport
{
    internal const string Why = "set KALSHI_INTEGRATION=1 (with KALSHI_ENV, KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH) to run read-only tests against the real Kalshi API";

    /// <summary>The settings and signer from the environment, or a skip if the key is not configured. Never prints the key.</summary>
    internal static (KalshiSettings Settings, KalshiSigner Signer) Credentials()
    {
        var settings = KalshiSettings.FromEnvironment();
        if (settings.KeyId is null || settings.PrivateKeyPath is null)
        {
            Assert.Skip("KALSHI_KEY_ID / KALSHI_PRIVATE_KEY_PATH are not configured");
        }

        Say($"target: {settings.Environment}, rest {settings.RestUrl.Host}, websocket {settings.WebSocketUrl.Host}");
        return (settings, KalshiSigner.FromSettings(settings));
    }

    /// <summary>Writes a line to the test output and to <c>live-summary.txt</c> next to the test assembly.</summary>
    internal static void Say(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "live-summary.txt"), line + Environment.NewLine);
    }
}
