using System.Text;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using NSec.Cryptography;

namespace KalTerm.Kalshi.Tests.Auth;

/// <summary>The reference's signer tests (kalshi-core/tests/test_auth.py), ported.</summary>
public class KalshiSignerTests
{
    private const string Path = "/trade-api/v2/portfolio/balance";
    private const long Ts = 1_700_000_000_000;

    private static string Pem() =>
        VectorFile.Load("ed25519.json").GetProperty("rawSign")[3].GetProperty("pkcs8Pem").GetString()!;

    [Fact]
    public void TheSignatureVerifiesOverTimestampMethodAndPath()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key);

        var signature = signer.Sign(Ts, "GET", Path);

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.GetPublicKey(), KeyBlobFormat.RawPublicKey);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes($"{Ts}GET{Path}"), Convert.FromBase64String(signature)));
    }

    [Fact]
    public void TheQueryStringIsExcludedAndTheMethodIsUppercased()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key);

        var withQuery = signer.Sign(Ts, "get", Path + "?limit=5&cursor=x");

        Assert.Equal(signer.Sign(Ts, "GET", Path), withQuery);
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.GetPublicKey(), KeyBlobFormat.RawPublicKey);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes($"{Ts}GET{Path}"), Convert.FromBase64String(withQuery)));
    }

    [Fact]
    public void TheQueryIsCutAtTheFirstQuestionMarkOnly()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key);

        Assert.Equal(signer.Sign(Ts, "GET", "/a"), signer.Sign(Ts, "GET", "/a?b?c"));
    }

    [Fact]
    public void HeadersCarryTheKeyIdTimestampAndSignature()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid-123", key);

        var headers = signer.Headers("GET", Path, Ts);

        Assert.Equal("kid-123", headers[KalshiSigner.KeyHeader]);
        Assert.Equal(Ts.ToString(CultureInfo.InvariantCulture), headers[KalshiSigner.TimestampHeader]);
        Assert.Equal(signer.Sign(Ts, "GET", Path), headers[KalshiSigner.SignatureHeader]);
        Assert.Equal(3, headers.Count);
    }

    [Fact]
    public void TheHeaderNamesMatchTheReference()
    {
        var names = VectorFile.Load("ed25519.json").GetProperty("headerNames").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal([KalshiSigner.KeyHeader, KalshiSigner.TimestampHeader, KalshiSigner.SignatureHeader], names);
    }

    [Fact]
    public void TheDefaultTimestampComesFromTheClockInMilliseconds()
    {
        var time = new AutoAdvanceTimeProvider();
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key, time);

        Assert.Equal("1700000000000", signer.Headers("GET", Path)[KalshiSigner.TimestampHeader]);

        time.Advance(TimeSpan.FromMilliseconds(2500));

        Assert.Equal("1700000002500", signer.Headers("GET", Path)[KalshiSigner.TimestampHeader]);
    }

    [Fact]
    public void TheDefaultTimestampWithTheRealClockIsInMilliseconds()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("k", key);

        var timestamp = long.Parse(signer.Headers("GET", Path)[KalshiSigner.TimestampHeader], CultureInfo.InvariantCulture);

        Assert.True(timestamp > 1_600_000_000_000);
    }

    [Fact]
    public void ToStringShowsTheKeyIdOnly()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());

        Assert.Equal("KalshiSigner(KeyId=kid-1)", new KalshiSigner("kid-1", key).ToString());
    }

    [Fact]
    public void ASignerOverAKeyItDoesNotOwnLeavesTheKeyAlone()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key);

        signer.Dispose();

        Assert.NotEmpty(key.Sign([1]));   // the key is still usable: the caller owns it
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        using var key = Ed25519PrivateKey.FromPem(Pem());
        var signer = new KalshiSigner("kid", key);

        Assert.ThrowsAny<ArgumentException>(() => new KalshiSigner(" ", key));
        Assert.Throws<ArgumentNullException>(() => new KalshiSigner("kid", null!));
        Assert.Throws<ArgumentNullException>(() => signer.Sign(Ts, null!, Path));
        Assert.Throws<ArgumentNullException>(() => signer.Sign(Ts, "GET", null!));
        Assert.Throws<ArgumentNullException>(() => KalshiSigner.FromSettings(null!));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("kid", null)]
    [InlineData(null, @"C:\keys\k.pem")]
    public void FromSettingsNeedsBothTheKeyIdAndTheKeyPath(string? keyId, string? path)
    {
        var settings = KalshiSettings.Create(keyId: keyId, privateKeyPath: path);

        var error = Assert.Throws<AuthException>(() => KalshiSigner.FromSettings(settings));

        Assert.Equal("KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH must both be set", error.Message);
    }

    [Fact]
    public void FromSettingsLoadsTheKeyFileAndOwnsTheKey()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("data.pem", Pem());
        var settings = KalshiSettings.Create(keyId: "kid", privateKeyPath: path);

        var signer = KalshiSigner.FromSettings(settings);
        var headers = signer.Headers("GET", Path, Ts);

        Assert.Equal("kid", headers[KalshiSigner.KeyHeader]);
        using var sameKey = Ed25519PrivateKey.FromPem(Pem());
        Assert.Equal(new KalshiSigner("kid", sameKey).Sign(Ts, "GET", Path), headers[KalshiSigner.SignatureHeader]);

        signer.Dispose();   // it owns the key it loaded, so the key is wiped
        Assert.Throws<ObjectDisposedException>(() => signer.Sign(Ts, "GET", Path));
    }

    [Fact]
    public void FromSettingsWithAMissingKeyFileIsAnAuthError()
    {
        using var directory = new TempDirectory();
        var settings = KalshiSettings.Create(keyId: "kid", privateKeyPath: System.IO.Path.Combine(directory.Path, "nope.pem"));

        Assert.Throws<AuthException>(() => KalshiSigner.FromSettings(settings));
    }
}
