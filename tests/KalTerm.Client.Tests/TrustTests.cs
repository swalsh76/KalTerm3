using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KalTerm.Client;
using KalTerm.Security;

namespace KalTerm.Client.Tests;

/// <summary>A tiny HTTPS server on loopback with a given certificate: it answers every request with an empty list of gaps and remembers the Authorization header it saw.</summary>
internal sealed class MiniTlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public MiniTlsServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? Authorization { get; private set; }

    public int Handshakes { get; private set; }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                Handshakes++;
                var request = new StringBuilder();
                var buffer = new byte[1024];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await tls.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Authorization = request.ToString().Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?["Authorization:".Length..].Trim();
                const string Body = "{\"gaps\":[]}";
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}"), _stop.Token);
                await tls.FlushAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that refuses the certificate hangs up during the handshake: that is the point of these tests.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _certificate.Dispose();
        _stop.Dispose();
    }
}

public sealed class TrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kalterm-trust-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private PinStore Pins() => new(Path.Combine(_dir, "known-hosts.json"));

    private static X509Certificate2 Certificate() => ServerCertificate.Create(["localhost"], [IPAddress.Loopback], TimeSpan.FromDays(30), DateTimeOffset.UtcNow);

    // ---- stores and addresses -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("srv01:5280", "srv01:5280")]
    [InlineData("https://SRV01:5280", "srv01:5280")]
    [InlineData("https://203.0.113.20:5280/", "203.0.113.20:5280")]
    [InlineData("https://srv01", "srv01:443")]
    public void AnAddressBecomesALowerCaseHostAndPortKey(string text, string key)
    {
        Assert.True(EndpointKey.TryParse(text, out var address));
        Assert.Equal(key, EndpointKey.Of(address));
    }

    [Theory]
    [InlineData("srv01")]
    [InlineData("srv01:")]
    [InlineData("srv01:notaport")]
    [InlineData("srv01:99999")]
    [InlineData("http://srv01:5280")]
    [InlineData("https://srv01:5280/v1")]
    [InlineData("")]
    [InlineData(null)]
    public void ABareHostWithoutAPortOrAnythingNotHttpsIsNotATrustTarget(string? text)
    {
        Assert.False(EndpointKey.TryParse(text, out _));
        Assert.Throws<ArgumentNullException>(() => EndpointKey.Of(null!));
    }

    [Fact]
    public void PinsAreRememberedPerServerAndReplacedWhenTrustedAgain()
    {
        var pins = Pins();
        var first = new string('A', 64);
        var second = new string('B', 64);
        Assert.Null(pins.Get("srv01:5280"));

        pins.Set("srv01:5280", first.ToLowerInvariant(), Now);
        pins.Set("other:5280", second, Now);
        pins.Set("srv01:5280", second, Now.AddDays(1));

        Assert.Equal(new Pin(second, Now.AddDays(1)), Pins().Get("srv01:5280"));
        Assert.Equal(["other:5280", "srv01:5280"], Pins().All().Keys.Order());
        Assert.True(pins.Remove("other:5280"));
        Assert.False(pins.Remove("other:5280"));
        Assert.Throws<ArgumentException>(() => pins.Set("srv01:5280", "not a fingerprint", Now));
        Assert.Throws<ArgumentException>(() => pins.Set(" ", first, Now));
    }

    [Fact]
    public void ADamagedPinFileIsReportedNotSilentlyTreatedAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "known-hosts.json");
        File.WriteAllText(path, "{ not json");

        var failure = Assert.Throws<InvalidDataException>(() => new PinStore(path).Get("x:1"));

        Assert.Contains("trust the servers again", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("KalTerm", "known-hosts.json"), PinStore.DefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public void TokensAreStoredProtectedAndNeverAsText()
    {
        var path = Path.Combine(_dir, "credentials.bin");
        var tokens = new TokenStore(path);
        Assert.Null(tokens.Get("srv01:5280"));

        tokens.Set("srv01:5280", "kt_secret-token-text");
        tokens.Set("other:5280", "kt_other");

        Assert.Equal("kt_secret-token-text", new TokenStore(path).Get("srv01:5280"));
        Assert.DoesNotContain("secret-token-text", Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        Assert.True(tokens.Remove("other:5280"));
        Assert.False(tokens.Remove("other:5280"));
        Assert.Throws<ArgumentException>(() => tokens.Set("srv01:5280", " "));
        Assert.EndsWith(Path.Combine("KalTerm", "credentials.bin"), TokenStore.DefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedTokenFileIsReported()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "credentials.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => new TokenStore(path).Get("x:1"));
    }

    [Fact]
    public void OnlyAnExactFingerprintMatchIsAccepted()
    {
        using var certificate = Certificate();
        var fingerprint = CertificateFingerprint.Of(certificate);

        Assert.True(PinnedTransport.Accepts(fingerprint, certificate, out var observed));
        Assert.Equal(fingerprint, observed);
        Assert.False(PinnedTransport.Accepts(null, certificate, out _));
        Assert.False(PinnedTransport.Accepts(new string('0', 64), certificate, out _));
        Assert.False(PinnedTransport.Accepts(fingerprint, null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ACertificateThatMatchesThePinIsRefusedOnceItHasExpired()
    {
        using var certificate = ServerCertificate.Create(["localhost"], [IPAddress.Loopback], TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
        var fingerprint = CertificateFingerprint.Of(certificate);
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());

        Assert.True(PinnedTransport.Accepts(fingerprint, certificate, notAfter.AddDays(-1), out _, out var none));
        Assert.Null(none);
        Assert.False(PinnedTransport.Accepts(fingerprint, certificate, notAfter.AddDays(1), out var observed, out var expiredOn));
        Assert.Equal(fingerprint, observed);
        Assert.Equal(notAfter, expiredOn);
        Assert.False(PinnedTransport.Accepts(new string('0', 64), certificate, notAfter.AddDays(1), out _, out var other));
        Assert.Null(other);
    }

    [Fact]
    public void TheExpiredMessageSaysWhenAndWhatToDo()
    {
        var thrown = new TlsTrustException(TrustFailure.Expired, "srv01:5280", "a", "a", new InvalidOperationException(), new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero), thrown.ExpiredOn);
        Assert.Contains("EXPIRED on 2026-03-04", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("kterm trust srv01:5280", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrustedCertificateThatHasExpiredIsAHardErrorAndNeverSendsTheRequest()
    {
        using var certificate = Certificate();
        await using var server = new MiniTlsServer(new X509Certificate2(certificate));
        server.Start();
        var address = new Uri($"https://localhost:{server.Port}");
        var pins = Pins();
        pins.Set(EndpointKey.Of(address), CertificateFingerprint.Of(certificate), Now);
        var later = new FixedTime(DateTimeOffset.UtcNow.AddDays(60));
        using var client = PinnedTransport.CreateClient(address, pins, "kt_the-token", null, later);

        var thrown = await Assert.ThrowsAsync<TlsTrustException>(() => client.GetGapsAsync(new WindowQuery(), TestContext.Current.CancellationToken));

        Assert.Equal(TrustFailure.Expired, thrown.Failure);
        Assert.NotNull(thrown.ExpiredOn);
        Assert.Null(server.Authorization);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- over a real TLS connection -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheProbeReadsTheCertificateTheServerPresentsWithoutSendingARequest()
    {
        using var certificate = Certificate();
        var expected = CertificateFingerprint.Of(certificate);
        await using var server = new MiniTlsServer(new X509Certificate2(certificate));
        server.Start();

        var identity = await TrustProbe.FetchAsync("localhost", server.Port, TestContext.Current.CancellationToken);

        Assert.Equal(expected, identity.Info.Fingerprint);
        Assert.Contains("127.0.0.1", identity.Info.Names);
        Assert.Null(server.Authorization);
    }

    [Fact]
    public async Task TheCertificateAsTheApiLoadsItFromDiskWorksAsAServerCertificate()
    {
        using var made = Certificate();
        ServerCertificate.Save(_dir, made);
        await using var server = new MiniTlsServer(ServerCertificate.Load(_dir));
        server.Start();
        var address = new Uri($"https://localhost:{server.Port}");
        var pins = Pins();
        pins.Set(EndpointKey.Of(address), CertificateFingerprint.Of(made), Now);
        using var client = PinnedTransport.CreateClient(address, pins, null);

        var gaps = await client.GetGapsAsync(new WindowQuery(), TestContext.Current.CancellationToken);

        Assert.Empty(gaps.Gaps);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsReportedByNameAndPort()
    {
        var failure = await Assert.ThrowsAsync<ApiUnavailableException>(() => TrustProbe.FetchAsync("127.0.0.1", 1, TestContext.Current.CancellationToken));

        Assert.Contains("127.0.0.1:1", failure.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => TrustProbe.FetchAsync(" ", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATrustedCertificateWorksAndTheTokenIsSentAsABearerHeader()
    {
        using var certificate = Certificate();
        await using var server = new MiniTlsServer(new X509Certificate2(certificate));
        server.Start();
        var address = new Uri($"https://localhost:{server.Port}");
        var pins = Pins();
        pins.Set(EndpointKey.Of(address), CertificateFingerprint.Of(certificate), Now);
        using var client = PinnedTransport.CreateClient(address, pins, "kt_the-token");

        var gaps = await client.GetGapsAsync(new WindowQuery(), TestContext.Current.CancellationToken);

        Assert.Empty(gaps.Gaps);
        Assert.Equal("Bearer kt_the-token", server.Authorization);
    }

    [Fact]
    public async Task AServerThatWasNeverTrustedIsRefusedBeforeAnythingIsSentAndTheErrorSaysHowToTrustIt()
    {
        using var certificate = Certificate();
        await using var server = new MiniTlsServer(new X509Certificate2(certificate));
        server.Start();
        var address = new Uri($"https://localhost:{server.Port}");
        using var client = PinnedTransport.CreateClient(address, Pins(), "kt_the-token");

        var failure = await Assert.ThrowsAsync<TlsTrustException>(() => client.GetGapsAsync(new WindowQuery(), TestContext.Current.CancellationToken));

        Assert.Equal((TrustFailure.NotTrusted, CertificateFingerprint.Of(certificate), null), (failure.Failure, failure.Observed, failure.Trusted));
        Assert.Contains($"kterm trust localhost:{server.Port}", failure.Message, StringComparison.Ordinal);
        Assert.Null(server.Authorization);   // the token never reached an untrusted server
    }

    [Fact]
    public async Task ADifferentCertificateFromTheTrustedOneIsAHardErrorThatNamesBothFingerprints()
    {
        using var served = Certificate();
        using var trusted = Certificate();
        await using var server = new MiniTlsServer(new X509Certificate2(served));
        server.Start();
        var address = new Uri($"https://localhost:{server.Port}");
        var pins = Pins();
        pins.Set(EndpointKey.Of(address), CertificateFingerprint.Of(trusted), Now);
        using var client = PinnedTransport.CreateClient(address, pins, "kt_the-token");

        var failure = await Assert.ThrowsAsync<TlsTrustException>(() => client.GetGapsAsync(new WindowQuery(), TestContext.Current.CancellationToken));

        Assert.Equal(TrustFailure.Changed, failure.Failure);
        Assert.Contains("CHANGED", failure.Message, StringComparison.Ordinal);
        Assert.Contains(CertificateFingerprint.Format(CertificateFingerprint.Of(trusted)), failure.Message, StringComparison.Ordinal);
        Assert.Contains(CertificateFingerprint.Format(CertificateFingerprint.Of(served)), failure.Message, StringComparison.Ordinal);
        Assert.Null(server.Authorization);
    }

    [Fact]
    public void AnHttpAddressNeedsNoPinAndNullsAreRefused()
    {
        using var client = PinnedTransport.CreateClient(new Uri("http://127.0.0.1:5280"), Pins(), null);

        Assert.NotNull(client);
        Assert.Throws<ArgumentNullException>(() => PinnedTransport.CreateClient(null!, Pins(), null));
        Assert.Throws<ArgumentNullException>(() => PinnedTransport.CreateClient(new Uri("https://x:1"), null!, null));
        Assert.NotEmpty(new TlsTrustException().Message);
        Assert.Equal("m", new TlsTrustException("m").Message);
        Assert.IsType<InvalidOperationException>(new TlsTrustException("m", new InvalidOperationException()).InnerException);
    }
}
