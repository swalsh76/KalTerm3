namespace KalTerm.Ingest.Tests;

public class RefreshScheduleTests
{
    private static DateTimeOffset At(int hour, int minute, int second, int millisecond = 0) =>
        new(2026, 6, 1, hour, minute, second, millisecond, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 59, true)]
    [InlineData(5, 0, false)]
    [InlineData(30, 0, false)]
    [InlineData(54, 59, false)]
    [InlineData(55, 0, true)]
    [InlineData(59, 59, true)]
    public void TheFastWindowIsTheLastAndFirstFiveMinutesOfTheHour(int minute, int second, bool fast)
    {
        Assert.Equal(fast, RefreshSchedule.InFastWindow(At(10, minute, second)));
    }

    [Theory]
    [InlineData(10, 0, 0, 10, 0, 30)]      // first window: every 30 s
    [InlineData(10, 0, 29, 10, 0, 30)]
    [InlineData(10, 2, 45, 10, 3, 0)]
    [InlineData(10, 4, 29, 10, 4, 30)]
    [InlineData(10, 4, 30, 10, 5, 0)]      // the window ends; :05:00 is also the first 5-minute tick
    [InlineData(10, 4, 59, 10, 5, 0)]
    [InlineData(10, 5, 0, 10, 10, 0)]      // normal cadence: every 5 minutes on the clock
    [InlineData(10, 5, 1, 10, 10, 0)]
    [InlineData(10, 7, 30, 10, 10, 0)]
    [InlineData(10, 49, 59, 10, 50, 0)]
    [InlineData(10, 50, 0, 10, 55, 0)]
    [InlineData(10, 52, 10, 10, 55, 0)]    // the next tick is the start of the last-five-minutes window
    [InlineData(10, 54, 59, 10, 55, 0)]
    [InlineData(10, 55, 0, 10, 55, 30)]    // second window: every 30 s
    [InlineData(10, 57, 31, 10, 58, 0)]
    [InlineData(10, 59, 29, 10, 59, 30)]
    [InlineData(10, 59, 30, 11, 0, 0)]     // the hour rolls over
    [InlineData(10, 59, 59, 11, 0, 0)]
    public void TheNextRunIsTheNextTickOnTheClock(int h, int m, int s, int eh, int em, int es)
    {
        Assert.Equal(At(eh, em, es), RefreshSchedule.NextRun(At(h, m, s)));
    }

    [Fact]
    public void AMomentJustBeforeATickWaitsForIt()
    {
        Assert.Equal(At(10, 10, 0), RefreshSchedule.NextRun(At(10, 9, 59, 999)));
    }

    [Fact]
    public void TheNextRunIsAlwaysAfterNowAndNeverMoreThanFiveMinutesAway()
    {
        var now = At(23, 0, 0);
        for (var i = 0; i < 24 * 120; i++)
        {
            var next = RefreshSchedule.NextRun(now);

            Assert.True(next > now);
            Assert.True(next - now <= TimeSpan.FromMinutes(5));
            now = now.AddSeconds(30);
        }
    }

    [Fact]
    public void TheWholeDayHasTheExpectedNumberOfTicks()
    {
        var count = 0;
        var tick = At(0, 0, 0).AddTicks(-1);
        var end = At(0, 0, 0).AddDays(1);
        while ((tick = RefreshSchedule.NextRun(tick)) < end)
        {
            count++;
        }

        // Per hour: 20 ticks in the two five-minute windows (:55:00 to :04:30), plus :05, :10 ... :50 = 10 more.
        Assert.Equal(24 * 30, count);
    }

    [Fact]
    public void OffsetsAreConvertedToUtcBeforeTheWindowIsTested()
    {
        var local = new DateTimeOffset(2026, 6, 1, 7, 2, 10, TimeSpan.FromHours(-5));   // 12:02:10 UTC

        Assert.Equal(At(12, 2, 30), RefreshSchedule.NextRun(local));
        Assert.Equal(TimeSpan.FromSeconds(20), RefreshSchedule.DelayUntilNext(local));
        Assert.True(RefreshSchedule.InFastWindow(local));
    }
}
