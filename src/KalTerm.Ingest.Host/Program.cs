using KalTerm.Ingest.Hosting;
using KalTerm.Kalshi.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The ingest process: market discovery and the ticker/trade stream into the database. Everything of substance is in KalTerm.Ingest
// (tested there); this file only wires configuration to it. Demo is the default; production needs KALSHI_ENV=production, set explicitly.
// The settings file sits next to the executable; a service or scheduled task starts in some other folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddEnvironmentVariables("KALTERM_");

var options = new IngestHostOptions();
builder.Configuration.GetSection(IngestHostOptions.SectionName).Bind(options);
options.Validate();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(KalshiSettings.FromEnvironment());
builder.Services.AddHostedService<IngestWorker>();

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
