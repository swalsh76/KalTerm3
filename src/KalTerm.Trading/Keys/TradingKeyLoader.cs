using KalTerm.Kalshi.Auth;

namespace KalTerm.Trading.Keys;

/// <summary>A signer built from the trading key, with the key it owns; dispose it to wipe the key from memory.</summary>
public sealed class TradingSigner : IDisposable
{
    private readonly Ed25519PrivateKey _key;

    internal TradingSigner(string environment, KalshiSigner signer, Ed25519PrivateKey key)
    {
        Environment = environment;
        Signer = signer;
        _key = key;
    }

    /// <summary>The Kalshi environment the key is for.</summary>
    public string Environment { get; }

    /// <summary>The signer for requests.</summary>
    public KalshiSigner Signer { get; }

    /// <summary>Wipes the key.</summary>
    public void Dispose()
    {
        Signer.Dispose();
        _key.Dispose();
    }
}

/// <summary>
/// Turns the stored trading key into a signer. A separate path from the data key's: the data key comes from environment variables and a file for the server and the read client, the trading key only from the
/// user's vault, so the two cannot be mixed up. Ed25519 only, unencrypted PKCS#8 PEM; anything else is refused by name, before it is stored.
/// </summary>
public static class TradingKeyLoader
{
    /// <summary>Checks that a PEM is a usable trading key without keeping it.</summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="source">What to call it in the message (a file name).</param>
    /// <exception cref="AuthException">It is not an unencrypted Ed25519 private key.</exception>
    public static void Validate(string pem, string source = "the key")
    {
        using var key = Ed25519PrivateKey.FromPem(pem, source);
    }

    /// <summary>Stores a key after checking it, replacing the one kept for the same environment.</summary>
    /// <param name="vault">Where keys are kept.</param>
    /// <param name="environment">demo or production.</param>
    /// <param name="keyId">The id Kalshi issued.</param>
    /// <param name="pem">The private key.</param>
    /// <param name="source">What to call the key in a message.</param>
    /// <exception cref="AuthException">The key is not an unencrypted Ed25519 key, or the id or the environment is unusable.</exception>
    public static void Store(ITradingKeyVault vault, string environment, string keyId, string pem, string source = "the key")
    {
        ArgumentNullException.ThrowIfNull(vault);
        CheckEnvironment(environment);
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Any(char.IsWhiteSpace))
        {
            throw new AuthException("the key id must be the id Kalshi issued, with no spaces");
        }

        Validate(pem, source);
        vault.Save(new TradingKeyEntry(environment.ToLowerInvariant(), keyId, pem));
    }

    /// <summary>Builds the signer for an environment from the vault.</summary>
    /// <param name="vault">Where keys are kept.</param>
    /// <param name="environment">demo or production.</param>
    /// <param name="time">The clock for timestamps; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <returns>The signer, or <see langword="null"/> when no key is kept for the environment.</returns>
    /// <exception cref="AuthException">The key kept is not usable.</exception>
    public static TradingSigner? Load(ITradingKeyVault vault, string environment, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        CheckEnvironment(environment);
        if (vault.Load(environment.ToLowerInvariant()) is not { } entry)
        {
            return null;
        }

        var key = Ed25519PrivateKey.FromPem(entry.Pem, $"the stored {entry.Environment} trading key");
        return new TradingSigner(entry.Environment, new KalshiSigner(entry.KeyId, key, time), key);
    }

    private static void CheckEnvironment(string environment)
    {
        if (!string.Equals(environment, "demo", StringComparison.OrdinalIgnoreCase) && !string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthException("the environment must be demo or production");
        }
    }
}
