using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>What the vectors cannot say: typed access, the private channels left for M6, and the snapshot's wire shape.</summary>
public class WsMessageTests
{
    private static WsMessage Envelope(string json) => KalshiJson.Parse(json, KalshiJsonContext.Default.WsMessage, nameof(WsMessage));

    [Fact]
    public void ATickerEnvelopeGivesATypedExactPayload()
    {
        var message = Envelope("""{"type":"ticker","sid":3,"seq":17,"msg":{"market_ticker":"A","price_dollars":"0.5600","volume_fp":"10.00"}}""");

        var ticker = Assert.IsType<TickerMsg>(message.Payload());

        Assert.Equal(17, message.Seq);
        Assert.Equal("A", ticker.MarketTicker);
        Assert.Equal(Dollars.Parse("0.56"), ticker.PriceDollars);
        Assert.Equal(Contracts.Parse("10"), ticker.VolumeFp);
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("user_order")]
    [InlineData("market_position")]
    [InlineData("subscribed")]
    [InlineData("brand_new_type")]
    public void TypesWithoutAModelHaveNoTypedPayloadButKeepTheRawMessage(string type)
    {
        var message = Envelope("{\"type\":\"" + type + "\",\"msg\":{\"a\":1}}");

        Assert.Null(message.Payload());
        Assert.Equal(1, message.Msg!.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void AKnownTypeWithoutAPayloadObjectIsAResponseError()
    {
        var message = Envelope("""{"type":"trade"}""");

        var error = Assert.Throws<KalshiResponseException>(() => message.Payload());

        Assert.Contains("trade", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentSnapshotSideIsEmptyButANullOneIsRefused()
    {
        var snapshot = KalshiJson.Parse("""{"market_ticker":"A"}""", KalshiJsonContext.Default.OrderbookSnapshotMsg, "snapshot");

        Assert.Empty(snapshot.YesDollarsFp);
        Assert.Empty(snapshot.NoDollarsFp);
        Assert.Throws<KalshiResponseException>(
            () => KalshiJson.Parse("""{"market_ticker":"A","yes_dollars_fp":null}""", KalshiJsonContext.Default.OrderbookSnapshotMsg, "snapshot"));
    }

    [Fact]
    public void ASnapshotWritesBackInKalshisPairShape()
    {
        var snapshot = KalshiJson.Parse(
            """{"market_ticker":"A","yes_dollars_fp":[["0.1500","100.00"],[0.5,2]],"no_dollars_fp":[]}""",
            KalshiJsonContext.Default.OrderbookSnapshotMsg,
            "snapshot");

        var written = JsonSerializer.SerializeToElement(snapshot, KalshiJsonContext.Default.OrderbookSnapshotMsg);

        Assert.Equal(new PriceLevel(Dollars.Parse("0.15"), Contracts.Parse("100")), snapshot.YesDollarsFp[0]);
        Assert.Equal("""[["0.150000","100.00"],["0.500000","2.00"]]""", written.GetProperty("yes_dollars_fp").GetRawText());
    }

    [Fact]
    public void AnOrderBookDeltaCanBeNegative()
    {
        var delta = KalshiJson.Parse(
            """{"market_ticker":"A","price_dollars":"0.15","delta_fp":"-25.50","side":"yes"}""", KalshiJsonContext.Default.OrderbookDeltaMsg, "delta");

        Assert.Equal(-2550, delta.DeltaFp.Hundredths);
    }
}
