using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>The exact fixed-point readers and the strict date-time reader.</summary>
public class JsonConverterTests
{
    private static Market MarketWith(string fragment)
    {
        var json = "{\"ticker\":\"T\",\"event_ticker\":\"E\",\"market_type\":\"binary\",\"status\":\"active\"," + fragment + "}";
        return KalshiJson.Parse(json, KalshiJsonContext.Default.Market, "market");
    }

    [Theory]
    [InlineData("\"0.5600\"", 560_000)]
    [InlineData("\"1\"", 1_000_000)]
    [InlineData("0.56", 560_000)]
    [InlineData("1", 1_000_000)]
    [InlineData("1e-3", 1_000)]
    [InlineData("5E-1", 500_000)]
    [InlineData("\"-0.5\"", -500_000)]
    [InlineData("0.000001", 1)]
    public void APriceIsReadExactlyFromAStringOrANumberToken(string token, long micros)
    {
        Assert.Equal(micros, MarketWith("\"yes_bid_dollars\":" + token).YesBidDollars!.Value.Micros);
    }

    [Theory]
    [InlineData("\"0.1234567\"")]
    [InlineData("0.0000001")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("\" 0.5 \"")]
    [InlineData("true")]
    [InlineData("[\"0.5\"]")]
    [InlineData("{\"v\":\"0.5\"}")]
    [InlineData("\"NaN\"")]
    public void APriceThatIsNotExactIsRefusedNeverRounded(string token)
    {
        var error = Assert.Throws<KalshiResponseException>(() => MarketWith("\"yes_bid_dollars\":" + token));

        Assert.Contains("yes_bid_dollars", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullPriceIsAbsent()
    {
        Assert.Null(MarketWith("\"yes_bid_dollars\":null").YesBidDollars);
    }

    [Fact]
    public void CountsUseTheirOwnScale()
    {
        Assert.Equal(1_050, MarketWith("\"volume_fp\":\"10.50\"").VolumeFp!.Value.Hundredths);
        Assert.Throws<KalshiResponseException>(() => MarketWith("\"volume_fp\":\"10.501\""));
    }

    [Theory]
    [InlineData("2026-10-07T21:22:01Z", 0)]
    [InlineData("2026-10-07T21:22:01.978691Z", 9_786_910)]
    [InlineData("2026-10-07T21:22:01.5Z", 5_000_000)]
    [InlineData("2026-10-07T21:22:01.123456789Z", 1_234_567)]   // nine digits: kept to 100 ns
    [InlineData("2026-10-07T21:22:01.1234567Z", 1_234_567)]
    public void FractionalSecondsAreKeptToTheTick(string text, long ticksIntoTheSecond)
    {
        var time = MarketWith("\"close_time\":\"" + text + "\"").CloseTime!.Value;

        Assert.Equal(ticksIntoTheSecond, time.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.Zero, time.Offset);
    }

    [Theory]
    [InlineData("2026-10-07T23:22:01+02:00", "2026-10-07T21:22:01Z")]
    [InlineData("2026-10-07T16:22:01-05:00", "2026-10-07T21:22:01Z")]
    [InlineData("2026-10-07T21:22:01+00:00", "2026-10-07T21:22:01Z")]
    public void AnOffsetIsHonouredAndTheInstantIsPreserved(string text, string sameInstant)
    {
        var time = MarketWith("\"close_time\":\"" + text + "\"").CloseTime!.Value;

        Assert.Equal(DateTimeOffset.Parse(sameInstant, CultureInfo.InvariantCulture), time);
    }

    [Theory]
    [InlineData("\"2026-10-07T21:22:01\"")]            // no offset: would be read as local time
    [InlineData("\"2026-10-07\"")]                     // a bare date
    [InlineData("\"2026-10-07 21:22:01Z\"")]           // a space instead of the T
    [InlineData("\"2026-10-07T21:22:01+0200\"")]       // offset without the colon
    [InlineData("\"2026-10-07T21:22:01.Z\"")]          // an empty fraction
    [InlineData("\"2026-10-07T21:22:01.1234567890Z\"")] // ten fractional digits
    [InlineData("\"2026-02-30T00:00:00Z\"")]           // not a real date
    [InlineData("\"2026-10-07T24:00:00Z\"")]           // hour 24
    [InlineData("\"2026-10-07T23:59:60Z\"")]           // a leap second
    [InlineData("\"2026-10-07T21:22:01+15:00\"")]      // an offset beyond +14:00
    [InlineData("\"yesterday\"")]
    [InlineData("\"\"")]
    [InlineData("1791334891")]                          // epoch seconds as a number
    [InlineData("true")]
    public void AnAmbiguousOrInvalidTimestampIsRefused(string token)
    {
        var error = Assert.Throws<KalshiResponseException>(() => MarketWith("\"close_time\":" + token));

        Assert.Contains("close_time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimestampRoundTripsThroughSerialisation()
    {
        var market = MarketWith("\"close_time\":\"2026-10-07T21:22:01.978691Z\"");

        var again = KalshiJson.Parse(
            JsonSerializer.Serialize(market, KalshiJsonContext.Default.Market), KalshiJsonContext.Default.Market, "market");

        Assert.Equal(market.CloseTime, again.CloseTime);
        Assert.Equal(market.YesBidDollars, again.YesBidDollars);
    }

    [Fact]
    public void ALevelRoundTripsThroughSerialisation()
    {
        var market = MarketWith("\"floor_strike\":118.7499");

        var again = KalshiJson.Parse(
            JsonSerializer.Serialize(market, KalshiJsonContext.Default.Market), KalshiJsonContext.Default.Market, "market");

        Assert.Equal(Level.Parse("118.7499"), again.FloorStrike);
    }

    [Fact]
    public void ANegativeFeeMultiplierIsRefused()
    {
        const string series = "{\"ticker\":\"S\",\"title\":\"t\",\"frequency\":\"daily\",\"category\":\"x\",\"fee_multiplier\":-1}";

        Assert.Throws<KalshiResponseException>(() => KalshiJson.Parse(series, KalshiJsonContext.Default.Series, "series"));
    }
}
