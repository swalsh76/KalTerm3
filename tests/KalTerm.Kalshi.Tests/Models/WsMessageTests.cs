using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>What the vectors cannot say: typed access,  the snapshot's wire shape.</summary>
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
    [InlineData("subscribed")]
    [InlineData("brand_new_type")]
    public void TypesWithoutAModelHaveNoTypedPayloadButKeepTheRawMessage(string type)
    {
        var message = Envelope("{\"type\":\"" + type + "\",\"msg\":{\"a\":1}}");

        Assert.Null(message.Payload());
        Assert.Equal(1, message.Msg!.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void AFillGivesATypedExactPayload()
    {
        var message = Envelope("""{"type":"fill","sid":1,"msg":{"trade_id":"t-1","order_id":"o-1","client_order_id":"c-1","market_ticker":"KXA-1","exchange_index":3,"is_taker":true,"book_side":"bid","yes_price_dollars":"0.4000","count_fp":"1.50","fee_cost":"0.0200","post_position_fp":"2.50","ts_ms":5,"extra":1}}""");

        var fill = Assert.IsType<FillMsg>(message.Payload());

        Assert.Equal(("t-1", "o-1", "c-1", "KXA-1", 3, true, "bid"), (fill.TradeId, fill.OrderId, fill.ClientOrderId, fill.MarketTicker, fill.ExchangeIndex, fill.IsTaker, fill.BookSide));
        Assert.Equal((400_000L, 150L, 20_000L, 250L), (fill.YesPriceDollars.Micros, fill.CountFp.Hundredths, fill.FeeCost.Micros, fill.PostPositionFp!.Value.Hundredths));
    }

    [Fact]
    public void AFillWithoutOptionalFieldsUsesDefaults()
    {
        var fill = Assert.IsType<FillMsg>(Envelope("""{"type":"fill","msg":{"trade_id":"t","order_id":"o","market_ticker":"K","yes_price_dollars":"0.4","count_fp":"1"}}""").Payload());

        Assert.Equal((0L, "", 0, null), (fill.FeeCost.Micros, fill.ClientOrderId, fill.ExchangeIndex, fill.PostPositionFp));
    }

    [Fact]
    public void AUserOrderGivesATypedExactPayload()
    {
        var order = Assert.IsType<UserOrderMsg>(Envelope("""{"type":"user_order","msg":{"order_id":"o-1","ticker":"KXA-1","status":"resting","book_side":"ask","yes_price_dollars":"0.3000","initial_count_fp":"2.00","fill_count_fp":"0.50","remaining_count_fp":"1.50","last_updated_ts_ms":1000}}""").Payload());

        Assert.Equal(("o-1", "resting", "ask", 300_000L, 200L, 50L, 150L, 1000L), (order.OrderId, order.Status, order.BookSide, order.YesPriceDollars.Micros, order.InitialCountFp.Hundredths, order.FillCountFp.Hundredths, order.RemainingCountFp.Hundredths, order.LastUpdatedTsMs));
    }

    [Fact]
    public void AMarketPositionGivesATypedExactPayloadWithOptionalFiguresAbsent()
    {
        var full = Assert.IsType<MarketPositionMsg>(Envelope("""{"type":"market_position","msg":{"market_ticker":"K","position_fp":"-4.00","position_cost_dollars":"1.9","realized_pnl_dollars":"-0.5"}}""").Payload());
        var bare = Assert.IsType<MarketPositionMsg>(Envelope("""{"type":"market_position","msg":{"market_ticker":"K","position_fp":"2.00"}}""").Payload());

        Assert.Equal((-400L, 1_900_000L, -500_000L), (full.PositionFp.Hundredths, full.PositionCostDollars!.Value.Micros, full.RealizedPnlDollars!.Value.Micros));
        Assert.Equal((null, null), (bare.PositionCostDollars, bare.RealizedPnlDollars));
    }

    [Theory]
    [InlineData("""{"type":"fill","msg":{"trade_id":"t","order_id":"o","market_ticker":"K","yes_price_dollars":"0.0000001","count_fp":"1"}}""")]
    [InlineData("""{"type":"fill","msg":{"trade_id":"t","order_id":"o","market_ticker":"K","yes_price_dollars":"0.4","count_fp":"1.001"}}""")]
    [InlineData("""{"type":"user_order","msg":{"order_id":"o","ticker":"K","status":"resting","yes_price_dollars":"0.4","initial_count_fp":"1","fill_count_fp":"0","remaining_count_fp":"0.001"}}""")]
    [InlineData("""{"type":"market_position","msg":{"market_ticker":"K","position_fp":"1.234"}}""")]
    [InlineData("""{"type":"fill","msg":{"order_id":"o","market_ticker":"K","yes_price_dollars":"0.4","count_fp":"1"}}""")]
    public void APrivateMessageWithTooMuchPrecisionOrAMissingKeyIsRefusedNotRounded(string json)
    {
        Assert.Throws<KalshiResponseException>(() => Envelope(json).Payload());
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
