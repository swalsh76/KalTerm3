using KalTerm.Kalshi.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest.Hosting;

/// <summary>
/// Runs the ingest process inside the Generic Host: it starts with the host and stops when the host does (Ctrl+C, SIGTERM, a service stop). A failure
/// that ends the process is logged, sets a non-zero exit code and stops the host, so a service manager can restart it.
/// </summary>
public sealed partial class IngestWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Action<int> _setExitCode;
    private readonly ILogger _logger;

    /// <summary>Creates the worker.</summary>
    /// <param name="options">The host settings.</param>
    /// <param name="settings">Kalshi environment and credentials.</param>
    /// <param name="loggerFactory">Creates the loggers.</param>
    /// <param name="lifetime">Stops the host when the process ends.</param>
    /// <param name="run">What to run; <see langword="null"/> for <see cref="IngestComposition.RunAsync(IngestHostOptions, KalshiSettings, ILoggerFactory, CancellationToken)"/>. A seam for tests.</param>
    /// <param name="setExitCode">Records the exit code; <see langword="null"/> for <see cref="Environment.ExitCode"/>. A seam for tests.</param>
    public IngestWorker(
        IngestHostOptions options,
        KalshiSettings settings,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime lifetime,
        Func<CancellationToken, Task>? run = null,
        Action<int>? setExitCode = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(lifetime);
        _run = run ?? (cancellationToken => IngestComposition.RunAsync(options, settings, loggerFactory, cancellationToken));
        _lifetime = lifetime;
        _setExitCode = setExitCode ?? (code => Environment.ExitCode = code);
        _logger = loggerFactory.CreateLogger<IngestWorker>();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _run(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogFailed(_logger, ex);
            _setExitCode(1);
        }

        // The process ended on its own (a failure, or nothing left to run): end the host too.
        _lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "the ingest process failed and is stopping")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
