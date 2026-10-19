using System.Diagnostics;
using KalTerm.Data.Writing;
using static KalTerm.Data.Tests.Writing.WriterTestKit;

namespace KalTerm.Data.Tests.Writing;

/// <summary>
/// Opt-in measurement (set KALTERM_THROUGHPUT=1) that sizes the writer's buffer: a sustained paced load, then an unpaced burst, against
/// the real database. It writes its numbers to throughput-summary.txt next to the test assembly. Not a pass/fail performance gate.
/// </summary>
public class WriterThroughputTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private const int PacedRowsPerSecond = 20_000;
    private const int PacedSeconds = 30;
    private const int BurstRows = 1_000_000;

    [Fact]
    public async Task SustainedAndBurstThroughput()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KALTERM_THROUGHPUT") == "1", "set KALTERM_THROUGHPUT=1 to run the throughput measurement");
        db.Require();
        var report = new List<string> { $"server {db.Options.Server}, buffer 500,000 rows, flush 5,000 rows or 1 s" };

        report.Add(await RunAsync("paced", PacedRowsPerSecond * PacedSeconds, PacedRowsPerSecond));
        report.Add(await RunAsync("burst", BurstRows, rowsPerSecond: 0));

        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "throughput-summary.txt"), report);
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }
    }

    private async Task<string> RunAsync(string name, int rows, int rowsPerSecond)
    {
        var marketBase = name == "paced" ? 100_000 : 200_000;
        await using var writer = Writers.Ticker(new DataSource(db.Options));
        writer.Start();
        var timer = Stopwatch.StartNew();
        var highWater = 0;
        for (var i = 0; i < rows; i++)
        {
            writer.TryWrite(new TickerRow(Epoch.AddMilliseconds(i), marketBase + (i % 5_000), 500_000 + i, 490_000, 510_000, 1_000, 1_000, i, i, 100));
            if (i % 1_000 == 0)
            {
                highWater = Math.Max(highWater, writer.Stats.Buffered);
                if (rowsPerSecond > 0)
                {
                    var due = TimeSpan.FromSeconds((double)i / rowsPerSecond);
                    if (due > timer.Elapsed)
                    {
                        await Task.Delay(due - timer.Elapsed, TestContext.Current.CancellationToken);
                    }
                }
            }
        }

        var produced = timer.Elapsed;
        await WaitUntilAsync(() => writer.Stats.Written + writer.Stats.Dropped >= rows);
        var total = timer.Elapsed;
        var stats = writer.Stats;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {rows:N0} rows offered in {produced.TotalSeconds:F1} s, all settled after {total.TotalSeconds:F1} s ({rows / total.TotalSeconds:N0} rows/s); written {stats.Written:N0}, dropped {stats.Dropped:N0}, buffer high-water {highWater:N0}, last batch {stats.LastWrite.TotalMilliseconds:F0} ms, slowest batch {stats.MaxWrite.TotalMilliseconds:F0} ms, retries {stats.Retries}");
    }
}
