using System.Text;
using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.Auth;

/// <summary>
/// Builds the signed headers Kalshi requires on REST requests and the WebSocket handshake. The signature is the
/// Ed25519 signature, base64-encoded, of <c>timestamp-in-milliseconds + METHOD + path</c>, with the query string
/// excluded from the path.
/// </summary>
public sealed class KalshiSigner : IDisposable
{
    public const string KeyHeader = "KALSHI-ACCESS-KEY";
    public const string TimestampHeader = "KALSHI-ACCESS-TIMESTAMP";
    public const string SignatureHeader = "KALSHI-ACCESS-SIGNATURE";

    private readonly Ed25519PrivateKey _key;
    private readonly TimeProvider _time;
    private readonly bool _ownsKey;

    /// <param name="keyId">The id Kalshi issued for this key.</param>
    /// <param name="key">The private key. The caller keeps ownership and disposes it.</param>
    /// <param name="timeProvider">The clock for default timestamps; <see cref="TimeProvider.System"/> when omitted.</param>
    public KalshiSigner(string keyId, Ed25519PrivateKey key, TimeProvider? timeProvider = null)
        : this(keyId, key, timeProvider, ownsKey: false)
    {
    }

    private KalshiSigner(string keyId, Ed25519PrivateKey key, TimeProvider? timeProvider, bool ownsKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(key);

        KeyId = keyId;
        _key = key;
        _time = timeProvider ?? TimeProvider.System;
        _ownsKey = ownsKey;
    }

    public string KeyId { get; }

    /// <summary>
    /// Builds a signer for the data key from the settings. Both the key id and the key file path must be set. The
    /// signer owns the key it loads and disposes it.
    /// </summary>
    public static KalshiSigner FromSettings(KalshiSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.KeyId is null || settings.PrivateKeyPath is null)
        {
            throw new AuthException($"{KalshiSettings.KeyIdVariable} and {KalshiSettings.PrivateKeyPathVariable} must both be set");
        }

        return new KalshiSigner(settings.KeyId, Ed25519PrivateKey.FromPemFile(settings.PrivateKeyPath), timeProvider, ownsKey: true);
    }

    /// <summary>Base64 signature over <c>timestamp + METHOD + path</c> (query string excluded).</summary>
    public string Sign(long timestampMs, string method, string path)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        var query = path.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = query < 0 ? path : path[..query];
        var message = Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{timestampMs}{method.ToUpperInvariant()}{withoutQuery}"));

        return Convert.ToBase64String(_key.Sign(message));
    }

    /// <summary>The three headers for one request. The timestamp defaults to now, in milliseconds since the Unix epoch.</summary>
    public IReadOnlyDictionary<string, string> Headers(string method, string path, long? timestampMs = null)
    {
        var timestamp = timestampMs ?? _time.GetUtcNow().ToUnixTimeMilliseconds();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyHeader] = KeyId,
            [TimestampHeader] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SignatureHeader] = Sign(timestamp, method, path),
        };
    }

    public void Dispose()
    {
        if (_ownsKey)
        {
            _key.Dispose();
        }
    }

    /// <summary>Shows the key id only, never key material.</summary>
    public override string ToString() => $"KalshiSigner(KeyId={KeyId})";
}
