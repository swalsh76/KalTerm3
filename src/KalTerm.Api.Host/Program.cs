using System.Net;
using System.Security.Cryptography.X509Certificates;
using KalTerm.Api;
using KalTerm.Data;
using KalTerm.Security;

// The read-only API process. Everything of substance is in KalTerm.Api (tested there); this file wires configuration to it.
// It always listens on a loopback address over plain HTTP (local checks); with Api:LanUrl it also serves the network over HTTPS with the server's self-signed
// certificate, and there every request needs a token. It refuses to run if it ends up listening anywhere it was not configured to.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddEnvironmentVariables("KALTERM_");

var options = new ApiHostOptions();
builder.Configuration.GetSection(ApiHostOptions.SectionName).Bind(options);
IPEndPoint endpoint;
IPEndPoint? lan;
X509Certificate2? certificate = null;
try
{
    options.Validate();
    endpoint = options.ListenEndPoint();
    lan = options.LanEndPoint();
    if (lan is not null)
    {
        certificate = ServerCertificate.Load(options.TlsDirectory);
    }
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
{
    // A settings mistake is reported in one line, not as a crash.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(endpoint);
    if (lan is not null)
    {
        kestrel.Listen(lan, listen => listen.UseHttps(certificate!));
    }
});
builder.Services.AddKalTermApi(new DataSource(options.ToConnectionOptions()), requireToken: options.RequireToken);

var lanAddress = lan?.ToString();
var fingerprint = certificate is null ? null : CertificateFingerprint.Format(CertificateFingerprint.Of(certificate));
var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        options.EnsureOnlyConfiguredAddresses(app.Urls);
    }
    catch (InvalidOperationException ex)
    {
        ApiHostLog.Refusing(app.Logger, ex);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
        return;
    }

    if (lanAddress is not null && fingerprint is not null)
    {
        ApiHostLog.Serving(app.Logger, lanAddress, fingerprint);
    }
});
app.UseKalTermApi();

await app.RunAsync();
return Environment.ExitCode;
