using KalTerm.Core.Money;
using static KalTerm.Ingest.Tests.IngestTestKit;

namespace KalTerm.Ingest.Tests;

public class MarketMapperTests
{
    [Fact]
    public void CopiesTheCatalogFieldsAndConvertsTimesToUtcAndPricesToMicros()
    {
        var market = Open("KXTEST-1") with
        {
            OpenTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.FromHours(-4)),
            CloseTime = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            LatestExpirationTime = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
            SettlementTs = new DateTimeOffset(2026, 6, 1, 21, 0, 0, TimeSpan.Zero),
            Result = "yes",
            SettlementValueDollars = Dollars.Parse("1.000000"),
        };

        var record = MarketMapper.ToRecord(market);

        Assert.Equal(("KXTEST-1", "EV-KXTEST-1", "binary", "active", "Yes", "No"), (record.Ticker, record.EventTicker, record.MarketType, record.Status, record.YesSubTitle, record.NoSubTitle));
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), record.OpenTime);
        Assert.Equal(new DateTime(2026, 6, 1, 20, 0, 0, DateTimeKind.Utc), record.CloseTime);
        Assert.Equal(new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), record.LatestExpirationTime);
        Assert.Equal(new DateTime(2026, 6, 1, 21, 0, 0, DateTimeKind.Utc), record.SettlementTs);
        Assert.Equal("yes", record.Result);
        Assert.Equal(1_000_000L, record.SettlementValueE6);
    }

    [Fact]
    public void AbsentOptionalFieldsStayNull()
    {
        var record = MarketMapper.ToRecord(Open("A"));

        Assert.Null(record.OpenTime);
        Assert.Null(record.CloseTime);
        Assert.Null(record.LatestExpirationTime);
        Assert.Null(record.SettlementTs);
        Assert.Null(record.SettlementValueE6);
        Assert.Equal(string.Empty, record.Result);
    }

    [Fact]
    public void ANullMarketIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => MarketMapper.ToRecord(null!));
    }
}
