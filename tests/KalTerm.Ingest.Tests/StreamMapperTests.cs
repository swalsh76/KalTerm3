using KalTerm.Core.Money;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;

namespace KalTerm.Ingest.Tests;

public class StreamMapperTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheServerTimestampWinsOverTheReceiveTime()
    {
        var ts = StreamMapper.Timestamp(1_760_000_000_123, Received);

        Assert.Equal(new DateTime(2025, 10, 9, 8, 53, 20, 123, DateTimeKind.Utc), ts);
        Assert.Equal(DateTimeKind.Utc, ts.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public void AMissingOrUnusableTimestampFallsBackToTheReceiveTime(long? tsMs)
    {
        Assert.Equal(Received.UtcDateTime, StreamMapper.Timestamp(tsMs, Received));
    }

    [Fact]
    public void ATickerKeepsEveryFieldInFixedPoint()
    {
        var message = new TickerMsg(
            "A-1",
            "id",
            Dollars.Parse("0.5600"),
            Dollars.Parse("0.5500"),
            Dollars.Parse("0.5700"),
            Contracts.Parse("10.00"),
            Contracts.Parse("20.50"),
            Contracts.Parse("1234.00"),
            Contracts.Parse("99.00"),
            Contracts.Parse("0.01"),
            1_760_000_000_000);

        var row = StreamMapper.ToRow(message, 42, Received);

        Assert.Equal(42, row.MarketId);
        Assert.Equal(560_000L, row.PriceE6);
        Assert.Equal(550_000L, row.YesBidE6);
        Assert.Equal(570_000L, row.YesAskE6);
        Assert.Equal(1_000L, row.YesBidSizeE2);
        Assert.Equal(2_050L, row.YesAskSizeE2);
        Assert.Equal(123_400L, row.VolumeE2);
        Assert.Equal(9_900L, row.OpenInterestE2);
        Assert.Equal(1L, row.LastTradeSizeE2);
    }

    [Fact]
    public void AFieldTheTickerOmittedStaysNull()
    {
        var row = StreamMapper.ToRow(new TickerMsg("A-1"), 1, Received);

        Assert.Equal(new TickerRow(Received.UtcDateTime, 1), row);
    }

    [Theory]
    [InlineData("yes", TakerSide.Yes)]
    [InlineData("NO", TakerSide.No)]
    [InlineData("maybe", TakerSide.Unknown)]
    [InlineData(null, TakerSide.Unknown)]
    public void ATradeMapsTheTakerSide(string? side, TakerSide expected)
    {
        var message = new TradeMsg("t-1", "A-1", Dollars.Parse("0.4000"), Dollars.Parse("0.6000"), Contracts.Parse("3.00"), side, IsBlockTrade: true);

        var row = StreamMapper.ToRow(message, 7, Received);

        Assert.Equal(new TradeRow(Received.UtcDateTime, 7, "t-1", 400_000, 600_000, 300, expected, true), row);
    }

    [Fact]
    public void NullMessagesAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => StreamMapper.ToRow((TickerMsg)null!, 1, Received));
        Assert.Throws<ArgumentNullException>(() => StreamMapper.ToRow((TradeMsg)null!, 1, Received));
    }
}
