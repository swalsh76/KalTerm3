using KalTerm.Core.Fees;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>The reference's model tests (test_rest.py, test_multivariate.py), ported, plus the exactness guarantees.</summary>
public class ModelTests
{
    private const string MarketJson = """
        {"ticker":"A","event_ticker":"EV-1","market_type":"binary","status":"active",
         "yes_bid_dollars":"0.5600","volume_fp":"10.00","result":"","unknown_future_field":1}
        """;

    private const string MveMarketJson = """
        {"ticker":"KXMVECROSSCATEGORY-S20264B622355CC-ABC","event_ticker":"KXMVECROSSCATEGORY-S20264B622355CC",
         "market_type":"binary","status":"active","mve_collection_ticker":"KXMVECROSSCATEGORY-R",
         "mve_selected_legs":[
           {"event_ticker":"KXATPMATCH-26OCT07COPTSI","market_ticker":"KXATPMATCH-26OCT07COPTSI-TSI","side":"yes"},
           {"event_ticker":"KXCONCACAFNLGAME-26OCT06ANTARU","market_ticker":"KXCONCACAFNLGAME-26OCT06ANTARU-ANT","side":"no",
            "yes_settlement_value_dollars":"1.0000"}],
         "is_provisional":false,"exchange_index":1}
        """;

    private static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class =>
        KalshiJson.Parse(json, info, typeof(T).Name);

    [Fact]
    public void MarketFieldsAreExactAndExtrasIgnored()
    {
        var market = Parse(MarketJson, KalshiJsonContext.Default.Market);

        Assert.Equal(Dollars.Parse("0.56"), market.YesBidDollars);
        Assert.Equal(560_000, market.YesBidDollars!.Value.Micros);
        Assert.Equal(Contracts.Parse("10"), market.VolumeFp);
        Assert.Equal(1_000, market.VolumeFp!.Value.Hundredths);
        Assert.Equal(string.Empty, market.Result);
    }

    [Fact]
    public void AbsentOptionalFieldsTakeTheirDefaults()
    {
        var market = Parse("""{"ticker":"T","event_ticker":"E","market_type":"binary","status":"active"}""", KalshiJsonContext.Default.Market);

        Assert.Equal(string.Empty, market.YesSubTitle);
        Assert.Equal(string.Empty, market.RulesPrimary);
        Assert.Null(market.YesBidDollars);
        Assert.Null(market.CloseTime);
        Assert.Null(market.FloorStrike);
        Assert.Null(market.IsProvisional);
        Assert.False(market.IsMultivariate);
        Assert.Null(market.MveSelectedLegs);
    }

    [Fact]
    public void MultivariateMarketLegsAreParsed()
    {
        var market = Parse(MveMarketJson, KalshiJsonContext.Default.Market);

        Assert.True(market.IsMultivariate);
        Assert.Equal("KXMVECROSSCATEGORY-R", market.MveCollectionTicker);
        Assert.Equal(2, market.MveSelectedLegs!.Count);
        Assert.Equal("KXATPMATCH-26OCT07COPTSI-TSI", market.MveSelectedLegs[0].MarketTicker);
        Assert.Null(market.MveSelectedLegs[0].YesSettlementValueDollars);
        Assert.Equal(Dollars.Parse("1.0000"), market.MveSelectedLegs[1].YesSettlementValueDollars);
        Assert.Equal(1, market.ExchangeIndex);
        Assert.False(market.IsProvisional);
    }

    [Fact]
    public void StrikesAreExactLevelsNotFloats()
    {
        var market = Parse(
            """{"ticker":"T","event_ticker":"E","market_type":"binary","status":"active","floor_strike":118.7499,"cap_strike":"120"}""",
            KalshiJsonContext.Default.Market);

        Assert.Equal(Level.Parse("118.7499"), market.FloorStrike);
        Assert.Equal(118_749_900, market.FloorStrike!.Value.Micros);
        Assert.Equal(Level.Parse("120"), market.CapStrike);
    }

    [Fact]
    public void TheTwentyFourHourVolumeUsesItsOddFieldName()
    {
        var market = Parse(
            """{"ticker":"T","event_ticker":"E","market_type":"binary","status":"active","volume_24h_fp":"512.25"}""",
            KalshiJsonContext.Default.Market);

        Assert.Equal(Contracts.Parse("512.25"), market.Volume24hFp);
    }

    [Fact]
    public void EventsAndSeriesParse()
    {
        var events = Parse(
            """{"events":[{"event_ticker":"EV-1","series_ticker":"S","title":"t","mutually_exclusive":true}],"cursor":""}""",
            KalshiJsonContext.Default.EventsPage);
        var series = Parse(
            """{"series":[{"ticker":"S","title":"t","frequency":"daily","category":"x","fee_type":"quadratic","fee_multiplier":0.07}]}""",
            KalshiJsonContext.Default.SeriesList);

        Assert.True(events.Events[0].MutuallyExclusive);
        Assert.Equal(string.Empty, events.Events[0].SubTitle);
        Assert.Equal("S", series.Series[0].Ticker);
        Assert.Equal(FeeMultiplier.Parse("0.07"), series.Series[0].FeeMultiplier);
        Assert.Equal(70_000, series.Series[0].FeeMultiplier!.Value.Micros);
    }

    [Fact]
    public void TheFeeMultiplierFeedsTheFeeCalculationDirectly()
    {
        var series = Parse(
            """{"ticker":"S","title":"t","frequency":"daily","category":"x","fee_type":"quadratic","fee_multiplier":1}""",
            KalshiJsonContext.Default.Series);

        var multiplier = KalshiFees.TakerMultiplier(series.FeeType, series.FeeMultiplier!.Value.Micros)!.Value;

        Assert.Equal(17_500, KalshiFees.TakerFee(multiplier, Contracts.One, Dollars.Parse("0.5")).Micros);   // 1.75 cents at 50 cents
    }

    [Fact]
    public void EventsCanCarryNestedMultivariateMarkets()
    {
        const string eventStart =
            "{\"events\":[{\"event_ticker\":\"E1\",\"series_ticker\":\"KXMVECROSSCATEGORY-SHARD1\",\"title\":\"Arsenal vs Coventry\"," +
            "\"sub_title\":\"\",\"collateral_return_type\":\"\",\"mutually_exclusive\":false,\"markets\":[";
        var page = Parse(eventStart + MveMarketJson + "]}],\"cursor\":\"c1\"}", KalshiJsonContext.Default.EventsPage);

        Assert.Equal("c1", page.Cursor);
        Assert.True(page.Events[0].Markets![0].IsMultivariate);
    }

    [Fact]
    public void TradesKeepExactValuesAndMicroseconds()
    {
        var page = Parse(
            """
            {"trades":[{"trade_id":"t1","ticker":"A","yes_price_dollars":"0.5900","no_price_dollars":"0.4100","count_fp":"149.83",
             "taker_side":"yes","taker_book_side":"bid","is_block_trade":false,"created_time":"2026-10-07T21:22:01.978691Z"}],"cursor":""}
            """,
            KalshiJsonContext.Default.TradesPage);
        var trade = page.Trades[0];

        Assert.Equal(Contracts.Parse("149.83"), trade.CountFp);
        Assert.Equal(14_983, trade.CountFp.Hundredths);
        Assert.Equal(Dollars.Parse("0.59"), trade.YesPriceDollars);
        Assert.Equal(978_691, trade.CreatedTime.Ticks / 10 % 1_000_000);   // microseconds survive
        Assert.Equal(TimeSpan.Zero, trade.CreatedTime.Offset);
        Assert.Equal("yes", trade.TakerSide);
        Assert.False(trade.IsBlockTrade);
    }

    [Fact]
    public void ExchangeStatusParsesAndAFailClosedDefaultAppliesToShardTransfers()
    {
        var status = Parse(
            """
            {"exchange_active":true,"trading_active":false,"exchange_index_statuses":[
             {"exchange_index":1,"description":"shard","exchange_active":true,"trading_active":true}]}
            """,
            KalshiJsonContext.Default.ExchangeStatus);

        Assert.True(status.ExchangeActive);
        Assert.False(status.TradingActive);
        Assert.Null(status.ExchangeEstimatedResumeTime);
        Assert.False(status.ExchangeIndexStatuses![0].IntraExchangeTransfersActive);   // absent means not allowed
    }

    [Fact]
    public void OrderBookLevelsAreExact()
    {
        var book = OrderBook.FromResponse("""{"orderbook_fp":{"yes_dollars":[["0.1500","100.00"]],"no_dollars":[]}}""");

        Assert.Equal(Dollars.Parse("0.15"), book.Yes[0].Price);
        Assert.Equal(Contracts.Parse("100"), book.Yes[0].Quantity);
        Assert.Empty(book.No);
    }

    [Fact]
    public void AnOrderBookNumberIsReadFromItsTextNotAsAFloat()
    {
        // The reference's Decimal(0.15) is the float's binary expansion; the port reads the token "0.15" exactly.
        var book = OrderBook.FromResponse("""{"orderbook_fp":{"yes_dollars":[[0.15,1]]}}""");

        Assert.Equal(150_000, book.Yes[0].Price.Micros);
        Assert.Equal(100, book.Yes[0].Quantity.Hundredths);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"orderbook":{}}""")]
    [InlineData("""{"orderbook_fp":null}""")]
    [InlineData("""{"orderbook_fp":{"yes_dollars":[["0.15"]]}}""")]
    [InlineData("""{"orderbook_fp":{"yes_dollars":{"a":1}}}""")]
    [InlineData("""{"orderbook_fp":{"yes_dollars":[["0.1234567","1"]]}}""")]
    public void AMalformedOrderBookIsAResponseError(string json)
    {
        Assert.Throws<KalshiResponseException>(() => OrderBook.FromResponse(json));
    }

    [Fact]
    public void ARequiredFieldMissingNamesWhatWasBeingRead()
    {
        var error = Assert.Throws<KalshiResponseException>(
            () => KalshiJson.Parse("""{"event_ticker":"E","market_type":"binary","status":"active"}""", KalshiJsonContext.Default.Market, "market KXFOO"));

        Assert.StartsWith("market KXFOO:", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public void AnEmptyOrBrokenBodyIsAResponseError(string json)
    {
        Assert.Throws<KalshiResponseException>(() => KalshiJson.Parse(json, KalshiJsonContext.Default.ExchangeStatus, "exchange status"));
    }

    [Fact]
    public void ModelsCompareByValue()
    {
        Assert.Equal(
            Parse(MarketJson, KalshiJsonContext.Default.Market).YesBidDollars,
            Parse(MarketJson.Replace("0.5600", "0.560000", StringComparison.Ordinal), KalshiJsonContext.Default.Market).YesBidDollars);
    }

    [Fact]
    public void SerialisingWritesPricesAsFixedPointStrings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Parse(MarketJson, KalshiJsonContext.Default.Market), KalshiJsonContext.Default.Market);

        Assert.Contains("\"yes_bid_dollars\":\"0.560000\"", json, StringComparison.Ordinal);
        Assert.Contains("\"volume_fp\":\"10.00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ticker\":\"A\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("is_multivariate", json, StringComparison.Ordinal);
    }
}
