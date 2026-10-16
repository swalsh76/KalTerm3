using System.Formats.Asn1;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace KalTerm.Kalshi.Auth;

/// <summary>
/// An Ed25519 private key, loaded from an unencrypted PKCS#8 PEM. Ed25519 only: RSA and every other key type is
/// refused by name. The key lives in NSec/libsodium protected memory and is created non-exportable, so the private
/// bytes cannot be read back out of this object. Dispose it to wipe the key. Signing is thread-safe.
/// </summary>
/// <remarks>All library-specific code is here, so swapping NSec for another Ed25519 implementation touches one class.</remarks>
public sealed class Ed25519PrivateKey : IDisposable
{
    private const string Ed25519Oid = "1.3.101.112";
    private const string OnlyEd25519 = "only Ed25519 keys are supported (create an Ed25519 key; RSA is intentionally not implemented)";

    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    private static readonly Dictionary<string, string> KnownAlgorithms = new(StringComparer.Ordinal)
    {
        ["1.2.840.113549.1.1.1"] = "RSA",
        ["1.2.840.10045.2.1"] = "EC",
        ["1.2.840.10040.4.1"] = "DSA",
        ["1.3.101.110"] = "X25519",
        ["1.3.101.111"] = "X448",
        ["1.3.101.113"] = "Ed448",
    };

    private readonly Key _key;

    private Ed25519PrivateKey(Key key)
    {
        _key = key;
    }

    /// <summary>The underlying NSec key; internal so tests can check its export policy.</summary>
    internal Key UnderlyingKey => _key;

    /// <summary>Loads a key from PEM text. <paramref name="source"/> names it in error messages (for example a file path).</summary>
    public static Ed25519PrivateKey FromPem(string? pem, string source = "the key")
    {
        ArgumentNullException.ThrowIfNull(source);
        var notPem = $"{source} is not an unencrypted PEM private key";

        if (string.IsNullOrWhiteSpace(pem)
            || !PemEncoding.TryFind(pem, out var fields)
            || !pem.AsSpan(fields.Label).SequenceEqual("PRIVATE KEY"))
        {
            throw new AuthException(notPem);   // also covers encrypted and public-key PEMs, as the reference does
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(pem[fields.Base64Data].ToString());
        }
        catch (FormatException ex)
        {
            throw new AuthException(notPem, ex);
        }

        string oid;
        try
        {
            oid = ReadAlgorithmOid(der);
        }
        catch (Exception ex) when (ex is AsnContentException or InvalidOperationException)
        {
            throw new AuthException(notPem, ex);
        }

        if (oid != Ed25519Oid)
        {
            var family = KnownAlgorithms.TryGetValue(oid, out var name) ? name : $"algorithm {oid}";
            throw new AuthException($"unsupported key type {family}: {OnlyEd25519}");
        }

        try
        {
            var parameters = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None };
            return new Ed25519PrivateKey(Key.Import(Algorithm, der, KeyBlobFormat.PkixPrivateKey, parameters));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new AuthException(notPem, ex);
        }
    }

    /// <summary>Loads a key from a PEM file. A missing or unreadable file is an <see cref="AuthException"/> naming the path.</summary>
    public static Ed25519PrivateKey FromPemFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw new AuthException($"cannot read private key file {path}: {ex.Message}", ex);
        }

        return FromPem(pem, path);
    }

    /// <summary>The raw 64-byte Ed25519 signature of <paramref name="message"/>.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => Algorithm.Sign(_key, message);

    /// <summary>The raw 32-byte public key.</summary>
    public byte[] GetPublicKey() => _key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    public void Dispose() => _key.Dispose();

    /// <summary>Never prints key material.</summary>
    public override string ToString() => "Ed25519PrivateKey(redacted)";

    /// <summary>Reads the algorithm OID from a PKCS#8 PrivateKeyInfo without touching the key bytes.</summary>
    private static string ReadAlgorithmOid(byte[] der)
    {
        var info = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        info.ReadInteger();                                   // version
        return info.ReadSequence().ReadObjectIdentifier();   // AlgorithmIdentifier.algorithm
    }
}
