using KalTerm.Data.Writing;
using static KalTerm.Data.Tests.Writing.WriterTestKit;

namespace KalTerm.Data.Tests.Writing;

public class BulkWriterTests
{
    private static BulkWriterOptions Options(int flushRows = 100, int maxBuffered = 1000) => new()
    {
        FlushRows = flushRows,
        MaxBufferedRows = maxBuffered,
        FlushInterval = TimeSpan.FromMilliseconds(250),
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(8),
        JitterFraction = 0.2,
        UnhealthyAfterFailures = 3,
        ShutdownTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task WritesABatchAsSoonAsItIsFull()
    {
        var sink = new FakeSink();
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 3), time);
        writer.Start();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(writer.TryWrite(Row(i)));
        }

        await WaitUntilAsync(() => sink.Batches.Count == 1);
        Assert.Equal([0, 1, 2], sink.Batches[0].Select(r => r.MarketId));
        Assert.Equal(3, writer.Stats.Written);
    }

    [Fact]
    public async Task WritesAPartialBatchOnlyWhenTheIntervalElapses()
    {
        var sink = new FakeSink();
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(), time);
        writer.Start();

        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));
        await WaitUntilAsync(() => time.PendingTimers >= 1 && writer.Stats.Buffered == 0);
        Assert.Empty(sink.Batches);

        await PumpUntilAsync(time, () => sink.Batches.Count == 1);
        Assert.Equal(2, sink.Batches[0].Count);
        Assert.Contains(TimeSpan.FromMilliseconds(250), time.Delays);
    }

    [Fact]
    public async Task RetriesRetryableFailuresWithDoublingBackoffAndLosesNothing()
    {
        var sink = new FakeSink();
        for (var i = 0; i < 3; i++)
        {
            sink.Failures.Enqueue(new TimeoutException("slow"));
        }

        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 2), time);
        writer.Start();

        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));
        await PumpUntilAsync(time, () => sink.Batches.Count == 1);

        Assert.Equal(4, sink.Attempts);
        Assert.Equal(3, writer.Stats.Retries);
        Assert.Equal(0, writer.Stats.Dropped);
        Assert.Equal(2, writer.Stats.Written);
        var backoffs = time.Delays.Where(d => d >= TimeSpan.FromSeconds(1)).ToList();
        Assert.Equal(3, backoffs.Count);
        Assert.InRange(backoffs[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2));
        Assert.InRange(backoffs[1], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.4));
        Assert.InRange(backoffs[2], TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.8));
    }

    [Fact]
    public async Task BackoffStopsGrowingAtTheCap()
    {
        var sink = new FakeSink();
        for (var i = 0; i < 6; i++)
        {
            sink.Failures.Enqueue(new IOException("down"));
        }

        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 1), time);
        writer.Start();

        writer.TryWrite(Row(0));
        await PumpUntilAsync(time, () => sink.Batches.Count == 1);

        var backoffs = time.Delays.Where(d => d >= TimeSpan.FromSeconds(1)).ToList();
        Assert.Equal(6, backoffs.Count);
        Assert.All(backoffs, d => Assert.True(d <= TimeSpan.FromSeconds(8 * 1.2)));
        Assert.True(backoffs[^1] >= TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task ReportsUnhealthyAfterConsecutiveFailuresAndRecoversOnSuccess()
    {
        var sink = new FakeSink { AlwaysFail = new TimeoutException("down") };
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 1), time);
        writer.Start();

        writer.TryWrite(Row(0));
        await PumpUntilAsync(time, () => !writer.Stats.Healthy);
        Assert.True(writer.Stats.ConsecutiveFailures >= 3);
        Assert.Equal("down", writer.Stats.LastError);

        sink.AlwaysFail = null;
        // The sink records the batch before the writer notes the success, so wait for the state asserted on, not just the batch.
        await PumpUntilAsync(time, () => sink.Batches.Count == 1 && writer.Stats.Healthy);
        Assert.True(writer.Stats.Healthy);
        Assert.Equal(0, writer.Stats.ConsecutiveFailures);
        Assert.NotNull(writer.Stats.LastSuccess);
    }

    [Fact]
    public async Task DropsABatchTheDatabaseRefusesAndKeepsGoing()
    {
        var sink = new FakeSink();
        sink.Failures.Enqueue(new InvalidOperationException("bad value"));
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        await using var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 2), time);
        writer.Start();

        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));
        await WaitUntilAsync(() => writer.Stats.PoisonBatches == 1);
        writer.TryWrite(Row(2));
        writer.TryWrite(Row(3));
        await WaitUntilAsync(() => sink.Batches.Count == 1);

        var stats = writer.Stats;
        Assert.Equal(2, stats.Dropped);
        Assert.Equal(2, stats.Written);
        Assert.Equal(0, stats.Retries);
        Assert.Equal(0, stats.ConsecutiveFailures);
        Assert.True(stats.Healthy);
        var range = writer.TakeDroppedRange();
        Assert.Equal(new DroppedRange(Epoch, Epoch.AddSeconds(1), 2), range);
    }

    [Fact]
    public void ADroppedRowIsCountedAndItsTimeKeptWhenTheBufferIsFull()
    {
        var sink = new FakeSink();
        var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 2, maxBuffered: 4));

        var accepted = Enumerable.Range(0, 7).Select(i => writer.TryWrite(Row(i))).ToList();

        Assert.Equal([true, true, true, true, false, false, false], accepted);
        Assert.Equal(4, writer.Stats.Buffered);
        Assert.Equal(3, writer.Stats.Dropped);
        Assert.Equal(new DroppedRange(Epoch.AddSeconds(4), Epoch.AddSeconds(6), 3), writer.TakeDroppedRange());
    }

    [Fact]
    public void TakingTheDroppedRangeResetsIt()
    {
        var writer = new BulkWriter<TickerRow>(new FakeSink(), TickerRowMapper.Instance, Options(flushRows: 1, maxBuffered: 1));
        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));

        Assert.NotNull(writer.TakeDroppedRange());
        Assert.Null(writer.TakeDroppedRange());
        Assert.Equal(1, writer.Stats.Dropped);
    }

    [Fact]
    public async Task ShutdownWritesWhatIsBufferedAndDisposesTheSink()
    {
        var sink = new FakeSink();
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(), time);
        writer.Start();
        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));
        writer.TryWrite(Row(2));

        await writer.DisposeAsync();

        Assert.Equal(3, sink.Batches.Sum(b => b.Count));
        Assert.Equal(0, writer.Stats.Dropped);
        Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task ShutdownGivesUpOnAStuckWriteAndCountsTheRowsAsDropped()
    {
        var sink = new FakeSink { Gate = new TaskCompletionSource() };
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 2), time);
        writer.Start();
        writer.TryWrite(Row(0));
        writer.TryWrite(Row(1));
        await WaitUntilAsync(() => sink.Attempts == 1);
        writer.TryWrite(Row(2));

        var dispose = writer.DisposeAsync().AsTask();
        await PumpUntilAsync(time, () => dispose.IsCompleted);
        await dispose;

        Assert.Equal(3, writer.Stats.Dropped);
        Assert.Equal(0, writer.Stats.Written);
        Assert.Equal(0, writer.Stats.Buffered);
    }

    [Fact]
    public async Task ShutdownDuringRetriesDropsTheBatchInHand()
    {
        var sink = new FakeSink { AlwaysFail = new TimeoutException("down") };
        var time = new AutoAdvanceTimeProvider(autoFire: false);
        var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options(flushRows: 1), time);
        writer.Start();
        writer.TryWrite(Row(0));
        await WaitUntilAsync(() => writer.Stats.Retries >= 1);

        var dispose = writer.DisposeAsync().AsTask();
        await PumpUntilAsync(time, () => dispose.IsCompleted);
        await dispose;

        Assert.Equal(1, writer.Stats.Dropped);
    }

    [Fact]
    public async Task RowsAfterShutdownAreRefusedAndCounted()
    {
        var writer = new BulkWriter<TickerRow>(new FakeSink(), TickerRowMapper.Instance, Options());
        writer.Start();
        await writer.DisposeAsync();

        Assert.False(writer.TryWrite(Row(0)));
        Assert.Equal(1, writer.Stats.Dropped);
    }

    [Fact]
    public async Task DisposingAWriterThatWasNeverStartedIsFine()
    {
        var sink = new FakeSink();
        var writer = new BulkWriter<TickerRow>(sink, TickerRowMapper.Instance, Options());

        await writer.DisposeAsync();

        Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task StartingTwiceIsRefused()
    {
        await using var writer = new BulkWriter<TickerRow>(new FakeSink(), TickerRowMapper.Instance, Options());
        writer.Start();

        Assert.Throws<InvalidOperationException>(writer.Start);
    }

    [Fact]
    public void NullDependenciesAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new BulkWriter<TickerRow>(null!, TickerRowMapper.Instance));
        Assert.Throws<ArgumentNullException>(() => new BulkWriter<TickerRow>(new FakeSink(), null!));
    }

    public static TheoryData<BulkWriterOptions> InvalidOptions =>
    [
        new BulkWriterOptions { FlushRows = 0 },
        new BulkWriterOptions { FlushRows = 10, MaxBufferedRows = 5 },
        new BulkWriterOptions { FlushInterval = TimeSpan.Zero },
        new BulkWriterOptions { InitialBackoff = TimeSpan.Zero },
        new BulkWriterOptions { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(1) },
        new BulkWriterOptions { JitterFraction = -0.1 },
        new BulkWriterOptions { UnhealthyAfterFailures = 0 },
        new BulkWriterOptions { ShutdownTimeout = TimeSpan.Zero },
    ];

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptionsAreRefused(BulkWriterOptions options)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BulkWriter<TickerRow>(new FakeSink(), TickerRowMapper.Instance, options));
    }

    [Fact]
    public void TheDefaultsAreTheApprovedValues()
    {
        var options = new BulkWriterOptions();

        options.Validate();
        Assert.Equal(5_000, options.FlushRows);
        Assert.Equal(TimeSpan.FromSeconds(1), options.FlushInterval);
        Assert.Equal(500_000, options.MaxBufferedRows);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxBackoff);
        Assert.Equal(5, options.UnhealthyAfterFailures);
    }
}
