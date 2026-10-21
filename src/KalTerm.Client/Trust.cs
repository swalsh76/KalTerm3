using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KalTerm.Security;

namespace KalTerm.Client;

/// <summary>How a server's address is written in the pin and token stores: lower-case host, colon, port.</summary>
public static class EndpointKey
{
    /// <summary>The key of an address.</summary>
    /// <param name="address">The API address.</param>
    /// <returns>For example <c>srv01:5280</c>.</returns>
    public static string Of(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return string.Create(CultureInfo.InvariantCulture, $"{address.Host.ToLowerInvariant()}:{address.Port}");
    }

    /// <summary>Reads <c>host:port</c> (or an <c>https://host:port</c> address) into an address.</summary>
    /// <param name="text">The text.</param>
    /// <param name="address">The address (https).</param>
    /// <returns>Whether the text names a host and a port.</returns>
    public static bool TryParse(string? text, out Uri address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var hasScheme = trimmed.Contains("://", StringComparison.Ordinal);
        var colon = trimmed.LastIndexOf(':');
        if (!hasScheme && (colon < 1 || !int.TryParse(trimmed.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535))
        {
            return false;   // a bare host:port must name its port
        }

        if (!Uri.TryCreate(hasScheme ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.PathAndQuery.Length > 1)
        {
            return false;
        }

        address = uri;
        return true;
    }
}

/// <summary>A server certificate the user has trusted.</summary>
/// <param name="Fingerprint">Its SHA-256 fingerprint.</param>
/// <param name="TrustedAt">When it was trusted (UTC).</param>
public sealed record Pin(string Fingerprint, DateTime TrustedAt);

/// <summary>The certificates the user trusts, by server: a small file in the user's profile. Nothing else is ever accepted for an https address (the usual certificate authorities do not apply).</summary>
/// <param name="path">The file.</param>
public sealed class PinStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The usual place: <c>%APPDATA%\KalTerm\known-hosts.json</c>.</summary>
    /// <returns>The path.</returns>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", "known-hosts.json");

    /// <summary>The trusted certificate of a server.</summary>
    /// <param name="key">The <see cref="EndpointKey"/>.</param>
    /// <returns>The pin, or <see langword="null"/> when none was trusted.</returns>
    public Pin? Get(string key) => Load().GetValueOrDefault(key);

    /// <summary>Every pin.</summary>
    /// <returns>The pins by server.</returns>
    public IReadOnlyDictionary<string, Pin> All() => Load();

    /// <summary>Trusts a certificate for a server, replacing any earlier pin.</summary>
    /// <param name="key">The server.</param>
    /// <param name="fingerprint">The certificate's fingerprint.</param>
    /// <param name="now">The current UTC time.</param>
    public void Set(string key, string fingerprint, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!CertificateFingerprint.TryNormalize(fingerprint, out var normalized))
        {
            throw new ArgumentException("not a certificate fingerprint", nameof(fingerprint));
        }

        var all = new Dictionary<string, Pin>(Load(), StringComparer.Ordinal) { [key] = new Pin(normalized, now) };
        Save(all);
    }

    /// <summary>Forgets a server's pin.</summary>
    /// <param name="key">The server.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string key)
    {
        var all = new Dictionary<string, Pin>(Load(), StringComparer.Ordinal);
        if (!all.Remove(key))
        {
            return false;
        }

        Save(all);
        return true;
    }

    private Dictionary<string, Pin> Load()
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, Pin>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Pin>>(File.ReadAllText(path), Options) ?? new Dictionary<string, Pin>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a valid list of trusted servers; delete it and trust the servers again", ex);
        }
    }

    private void Save(Dictionary<string, Pin> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The API tokens of this Windows user, one per server, protected so only this user on this computer can read them.</summary>
/// <param name="path">The file.</param>
public sealed class TokenStore(string path)
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("KalTerm client tokens v1");

    /// <summary>The usual place: <c>%APPDATA%\KalTerm\credentials.bin</c>.</summary>
    /// <returns>The path.</returns>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", "credentials.bin");

    /// <summary>The token for a server.</summary>
    /// <param name="key">The <see cref="EndpointKey"/>.</param>
    /// <returns>The token, or <see langword="null"/>.</returns>
    public string? Get(string key) => Load().GetValueOrDefault(key);

    /// <summary>Stores the token for a server, replacing any earlier one.</summary>
    /// <param name="key">The server.</param>
    /// <param name="token">The token.</param>
    public void Set(string key, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Save(new Dictionary<string, string>(Load(), StringComparer.Ordinal) { [key] = token });
    }

    /// <summary>Forgets the token for a server.</summary>
    /// <param name="key">The server.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string key)
    {
        var all = new Dictionary<string, string>(Load(), StringComparer.Ordinal);
        if (!all.Remove(key))
        {
            return false;
        }

        Save(all);
        return true;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException($"{path} cannot be read by this Windows user; delete it and log in again", ex);
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        File.WriteAllBytes(temp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Why a connection to an https server was refused before any request was made.</summary>
public enum TrustFailure
{
    /// <summary>No certificate of that server was ever trusted.</summary>
    NotTrusted,

    /// <summary>The server presented a different certificate from the one trusted: a rotation, or something else answering.</summary>
    Changed,
}

/// <summary>The server's certificate is not the one the user trusted (or none was trusted yet). The message says which, and what to do.</summary>
public sealed class TlsTrustException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TlsTrustException()
        : base("the server's certificate is not trusted")
    {
        Endpoint = string.Empty;
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public TlsTrustException(string message)
        : base(message)
    {
        Endpoint = string.Empty;
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public TlsTrustException(string message, Exception innerException)
        : base(message, innerException)
    {
        Endpoint = string.Empty;
    }

    /// <summary>Creates the exception for a server.</summary>
    /// <param name="failure">Why.</param>
    /// <param name="endpoint">The server (<c>host:port</c>).</param>
    /// <param name="trusted">The fingerprint that was trusted, if any.</param>
    /// <param name="observed">The fingerprint the server presented.</param>
    /// <param name="inner">The cause.</param>
    public TlsTrustException(TrustFailure failure, string endpoint, string? trusted, string? observed, Exception inner)
        : base(Describe(failure, endpoint, trusted, observed), inner)
    {
        Failure = failure;
        Endpoint = endpoint;
        Trusted = trusted;
        Observed = observed;
    }

    /// <summary>Why.</summary>
    public TrustFailure Failure { get; }

    /// <summary>The server.</summary>
    public string Endpoint { get; }

    /// <summary>The fingerprint that was trusted, if any.</summary>
    public string? Trusted { get; }

    /// <summary>The fingerprint the server presented.</summary>
    public string? Observed { get; }

    private static string Describe(TrustFailure failure, string endpoint, string? trusted, string? observed) => failure == TrustFailure.NotTrusted
        ? $"{endpoint} has not been trusted yet; check its certificate fingerprint with the person who runs the server and run: kterm trust {endpoint}"
        : $"WARNING: the certificate of {endpoint} CHANGED. Trusted {Show(trusted)}, but the server presented {Show(observed)}. If the server's certificate was rotated on purpose, confirm the new fingerprint and run: kterm trust {endpoint}. Otherwise do not continue: something else may be answering.";

    private static string Show(string? fingerprint) => fingerprint is null ? "(none)" : CertificateFingerprint.Format(fingerprint);
}

/// <summary>What a server's certificate says, as fetched for the user to confirm.</summary>
/// <param name="Info">The certificate's description.</param>
public sealed record ServerIdentity(CertificateInfo Info);

/// <summary>The part of the connection that accepts only a pinned certificate.</summary>
public static class PinnedTransport
{
    /// <summary>Whether a presented certificate is the pinned one.</summary>
    /// <param name="pinned">The trusted fingerprint, if any.</param>
    /// <param name="presented">The certificate the server presented.</param>
    /// <param name="observed">Its fingerprint.</param>
    /// <returns>True only when it matches the pin exactly.</returns>
    public static bool Accepts(string? pinned, X509Certificate? presented, out string? observed)
    {
        observed = presented is null ? null : CertificateFingerprint.Of(presented);
        return pinned is not null && observed is not null && string.Equals(pinned, observed, StringComparison.Ordinal);
    }

    /// <summary>Makes a client for an address: https addresses accept only the pinned certificate; http ones (loopback) are plain.</summary>
    /// <param name="address">The API address.</param>
    /// <param name="pins">The trusted certificates.</param>
    /// <param name="token">The API token to send, if any.</param>
    /// <param name="timeout">How long one call may take.</param>
    /// <returns>The client.</returns>
    public static KalTermApiClient CreateClient(Uri address, PinStore pins, string? token, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(pins);
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            return KalTermApiClient.Create(address, null, timeout, token);
        }

        var key = EndpointKey.Of(address);
        var pinned = pins.Get(key)?.Fingerprint;
        var seen = new Seen();
        var sockets = new SocketsHttpHandler();
        sockets.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            var accepted = Accepts(pinned, certificate, out var observed);
            seen.Fingerprint = observed;
            return accepted;
        };
        return KalTermApiClient.Create(address, new TranslatingHandler(sockets, key, pinned, seen), timeout, token);
    }

    private sealed class Seen
    {
        public string? Fingerprint { get; set; }
    }

    private sealed class TranslatingHandler(HttpMessageHandler inner, string key, string? pinned, Seen seen) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && seen.Fingerprint is not null)
            {
                throw new TlsTrustException(pinned is null ? TrustFailure.NotTrusted : TrustFailure.Changed, key, pinned, seen.Fingerprint, ex);
            }
        }
    }
}

/// <summary>Fetches a server's certificate so the user can compare its fingerprint with the one the server's operator gave, before trusting it.</summary>
public static class TrustProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Connects, reads the certificate and disconnects. No request is sent.</summary>
    /// <param name="host">The server.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the certificate says.</returns>
    /// <exception cref="ApiUnavailableException">The server could not be reached or presented no certificate.</exception>
    public static async Task<ServerIdentity> FetchAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        X509Certificate2? captured = null;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
#pragma warning disable CA5359 // The probe only reads the certificate for the user to confirm; it sends nothing over this connection.
            await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                captured = certificate is null ? null : new X509Certificate2(certificate);
                return true;   // only to read the certificate: nothing is sent over this connection
            });
#pragma warning restore CA5359
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiUnavailableException($"could not read the certificate of {host}:{port}: {ex.Message}", ex);
        }

        using (captured)
        {
            return captured is null ? throw new ApiUnavailableException($"{host}:{port} presented no certificate") : new ServerIdentity(CertificateInfo.Of(captured));
        }
    }
}
