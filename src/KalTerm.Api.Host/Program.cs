using System.Net;
using KalTerm.Api;
using KalTerm.Data;

// The read-only API process. Everything of substance is in KalTerm.Api (tested there); this file wires configuration to it.
// Until TLS and tokens exist (M4) it listens on a loopback address only, and it refuses to run if any bound address is not loopback.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddEnvironmentVariables("KALTERM_");

var options = new ApiHostOptions();
builder.Configuration.GetSection(ApiHostOptions.SectionName).Bind(options);
IPEndPoint endpoint;
try
{
    options.Validate();
    endpoint = options.ListenEndPoint();
}
catch (InvalidOperationException ex)
{
    // A settings mistake is reported in one line, not as a crash.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(endpoint));
builder.Services.AddKalTermApi(new DataSource(options.ToConnectionOptions()));

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Urls;
    try
    {
        ApiHostOptions.EnsureAllLoopback(addresses);
    }
    catch (InvalidOperationException ex)
    {
        ApiHostLog.Refusing(app.Logger, ex);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    }
});
app.UseKalTermApi();

await app.RunAsync();
return Environment.ExitCode;
