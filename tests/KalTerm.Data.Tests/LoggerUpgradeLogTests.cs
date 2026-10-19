using Microsoft.Extensions.Logging;

namespace KalTerm.Data.Tests;

public class LoggerUpgradeLogTests
{
    [Fact]
    public void ForwardsEachLevelWithItsFormattedText()
    {
        var logger = new ListLogger();
        var log = new LoggerUpgradeLog(logger);

        log.LogTrace("trace {0}", 1);
        log.LogDebug("debug {0}", 2);
        log.LogInformation("info {0}", 3);
        log.LogWarning("warn {0}", 4);
        log.LogError("error {0}", 5);

        Assert.Equal(["trace 1", "debug 2", "info 3", "warn 4", "error 5"], logger.Messages);
        Assert.Equal([LogLevel.Debug, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error], logger.Levels);
    }

    [Fact]
    public void ForwardsAnExceptionWithTheErrorAndPassesTextWithoutArgumentsThrough()
    {
        var logger = new ListLogger();
        var cause = new InvalidOperationException("boom");

        new LoggerUpgradeLog(logger).LogError(cause, "failed {braces}");

        Assert.Same(cause, logger.LastException);
        Assert.Equal(["failed {braces}"], logger.Messages);
    }

    [Fact]
    public void DoesNothingWithoutALogger()
    {
        var log = new LoggerUpgradeLog(null);

        log.LogInformation("nobody listens {0}", 1);
        log.LogError(new InvalidOperationException(), "nor here");
    }
}
