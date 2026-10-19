using System.Net;
using KalTerm.Data;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api;

/// <summary>What the API process reads from its configuration (the <c>Api</c> section of <c>appsettings.json</c> or <c>KALTERM_Api__*</c> variables).</summary>
public sealed class ApiHostOptions
{
    /// <summary>The name of the configuration section.</summary>
    public const string SectionName = "Api";

    /// <summary>The address the API listens on unless configured otherwise: loopback, port 5280.</summary>
    public const string DefaultUrl = "http://127.0.0.1:5280";

    /// <summary>
    /// The plain-HTTP listener, on this machine only: it must be a loopback IP address. It serves the same API as the LAN listener, but a request that comes in here without a
    /// token is answered only for <c>/healthz</c>, <c>/readyz</c> and <c>/v1/status</c>, so local checks keep working without a token.
    /// </summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>
    /// The address the LAN reaches the API on (<c>Api:LanUrl</c>), for example <c>https://192.168.1.20:5280</c>: HTTPS with an explicit port and one specific IP address (never a wildcard).
    /// Everything there needs a token. Empty means the API is not reachable from the network at all.
    /// </summary>
    public string? LanUrl { get; set; }

    /// <summary>The folder with the server's TLS certificate (<c>Api:TlsDirectory</c>), made by <c>kalterm-admin cert init</c>.</summary>
    public string TlsDirectory { get; set; } = @"C:\KalTerm\tls";

    /// <summary>Whether requests need a token. Only ever switched off for a development run on loopback; the process refuses to serve the LAN without it.</summary>
    public bool RequireToken { get; set; } = true;

    /// <summary>The SQL Server to read from; shared memory on this machine by default. Windows authentication only.</summary>
    public string Server { get; set; } = DatabaseConnectionOptions.SharedMemoryServer;

    /// <summary>The database name.</summary>
    public string Database { get; set; } = DatabaseConnectionOptions.DefaultDatabase;

    /// <summary>The database connection options these settings describe.</summary>
    /// <returns>The options.</returns>
    public DatabaseConnectionOptions ToConnectionOptions() => new() { Server = Server, Database = Database, ApplicationName = "KalTerm.Api" };

    /// <summary>The LAN address to listen on, checked, or <see langword="null"/> when none is configured: <c>https</c>, one specific IP literal, an explicit port.</summary>
    /// <returns>The address and port.</returns>
    /// <exception cref="InvalidOperationException">The URL is not an <c>https</c> address with an IP address and an explicit port.</exception>
    public IPEndPoint? LanEndPoint()
    {
        if (string.IsNullOrWhiteSpace(LanUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(LanUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsDefaultPort)
        {
            throw new InvalidOperationException("Api:LanUrl must be an https address with an explicit port, for example https://192.168.1.20:5280");
        }

        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new InvalidOperationException("Api:LanUrl must name one specific IP address, not a host name or a wildcard");
        }

        return new IPEndPoint(address, uri.Port);
    }

    /// <summary>The address to listen on, checked: <c>http</c>, a loopback IP literal, a port.</summary>
    /// <returns>The address and port.</returns>
    /// <exception cref="InvalidOperationException">The URL is not an <c>http</c> loopback address with a port.</exception>
    public IPEndPoint ListenEndPoint()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsDefaultPort)
        {
            throw new InvalidOperationException("Api:Url must be an http address with an explicit port, for example " + DefaultUrl);
        }

        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Api:Url must be a loopback IP address (127.0.0.1 or [::1]); the network address is Api:LanUrl");
        }

        return new IPEndPoint(address, uri.Port);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if the settings cannot work.</summary>
    public void Validate()
    {
        _ = ListenEndPoint();
        if (LanEndPoint() is not null && !RequireToken)
        {
            throw new InvalidOperationException("the API refuses to serve the network without tokens (Api:RequireToken must be true when Api:LanUrl is set)");
        }

        if (LanEndPoint() is not null && string.IsNullOrWhiteSpace(TlsDirectory))
        {
            throw new InvalidOperationException("Api:TlsDirectory must be set to serve the network");
        }

        if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("Api:Server and Api:Database must not be empty");
        }
    }

    /// <summary>
    /// Throws if the server is listening anywhere it was not told to: a loopback IP over http, or exactly the configured LAN address over https. The last line of defense against a
    /// configuration that listens wider than intended.
    /// </summary>
    /// <param name="addresses">The addresses the server reports, as URLs.</param>
    /// <exception cref="InvalidOperationException">An address is neither loopback nor the configured LAN address.</exception>
    public void EnsureOnlyConfiguredAddresses(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var lan = LanEndPoint();
        foreach (var address in addresses)
        {
            var allowed = Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
                && ((uri.Scheme == Uri.UriSchemeHttp && IPAddress.IsLoopback(ip)) || (uri.Scheme == Uri.UriSchemeHttps && lan is not null && ip.Equals(lan.Address) && uri.Port == lan.Port));
            if (!allowed)
            {
                throw new InvalidOperationException($"the API is listening on '{address}', which was not configured");
            }
        }
    }
}

/// <summary>Log messages of the API process.</summary>
public static partial class ApiHostLog
{
    /// <summary>The API started on its network address; clients pin this certificate fingerprint.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="address">The address.</param>
    /// <param name="fingerprint">The certificate's fingerprint.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "serving the network on https://{Address}; clients pin the certificate with fingerprint {Fingerprint}")]
    public static partial void Serving(ILogger logger, string address, string fingerprint);

    /// <summary>The process found itself listening on an address it was not configured for and is stopping.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What was found.</param>
    [LoggerMessage(Level = LogLevel.Critical, Message = "refusing to run: the API is listening where it was not configured to")]
    public static partial void Refusing(ILogger logger, Exception exception);
}
