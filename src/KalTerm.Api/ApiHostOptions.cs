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

    /// <summary>Where to listen. Until TLS and tokens exist (M4) this must be a loopback IP address, and the process refuses anything else.</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>The SQL Server to read from; shared memory on this machine by default. Windows authentication only.</summary>
    public string Server { get; set; } = DatabaseConnectionOptions.SharedMemoryServer;

    /// <summary>The database name.</summary>
    public string Database { get; set; } = DatabaseConnectionOptions.DefaultDatabase;

    /// <summary>The database connection options these settings describe.</summary>
    /// <returns>The options.</returns>
    public DatabaseConnectionOptions ToConnectionOptions() => new() { Server = Server, Database = Database, ApplicationName = "KalTerm.Api" };

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
            throw new InvalidOperationException("Api:Url must be a loopback IP address (127.0.0.1 or [::1]) until TLS and tokens exist; the API is not exposed beyond this machine");
        }

        return new IPEndPoint(address, uri.Port);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if the settings cannot work.</summary>
    public void Validate()
    {
        _ = ListenEndPoint();
        if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("Api:Server and Api:Database must not be empty");
        }
    }

    /// <summary>Throws if any address the server actually bound is not loopback; the last line of defense against a configuration that listens wider than intended.</summary>
    /// <param name="addresses">The addresses the server reports, as URLs.</param>
    /// <exception cref="InvalidOperationException">An address is not a loopback IP.</exception>
    public static void EnsureAllLoopback(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip))
            {
                throw new InvalidOperationException($"the API is listening on '{address}', which is not a loopback address");
            }
        }
    }
}

/// <summary>Log messages of the API process.</summary>
public static partial class ApiHostLog
{
    /// <summary>The process found itself listening on a non-loopback address and is stopping.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What was found.</param>
    [LoggerMessage(Level = LogLevel.Critical, Message = "refusing to run: the API must listen on loopback only")]
    public static partial void Refusing(ILogger logger, Exception exception);
}
