using System.Reflection;
using KalTerm.Contracts;

namespace KalTerm.Contracts.Tests;

/// <summary>
/// The wire contract, pinned: every model serializes to exactly this JSON (snake_case, nulls written, money as strings) and reads back to an equal value.
/// A deliberate change to the contract changes a line here, and nothing else can.
/// </summary>
public class ContractTests
{
    private static void Pin<T>(T value, string json)
        where T : class
    {
        Assert.Equal(json, ApiJson.Serialize(value));
        var back = ApiJson.Deserialize<T>(json)!;
        Assert.Equal(json, ApiJson.Serialize(back));
    }

    [Fact]
    public void AMarket()
    {
        Pin(
            new MarketDto("KXHIGHNY-26OCT19-T70", "KXHIGHNY-26OCT19", "binary", "active", "Yes", "No", "2026-10-18T14:00:00.000000+00:00", "2026-10-19T23:00:00.000000+00:00", null, string.Empty, null, null, "2026-10-18T22:23:30.123000+00:00", "2026-10-18T22:53:30.000000+00:00"),
            """{"ticker":"KXHIGHNY-26OCT19-T70","event_ticker":"KXHIGHNY-26OCT19","type":"binary","status":"active","yes_label":"Yes","no_label":"No","open_time":"2026-10-18T14:00:00.000000+00:00","close_time":"2026-10-19T23:00:00.000000+00:00","expiration_time":null,"result":"","settlement_value":null,"settlement_time":null,"first_seen_at":"2026-10-18T22:23:30.123000+00:00","last_seen_at":"2026-10-18T22:53:30.000000+00:00"}""");
    }

    [Fact]
    public void APageOfMarketsWithACursor()
    {
        var market = new MarketDto("A", "E", "binary", "active", "Yes", "No", null, null, null, string.Empty, null, null, "2026-10-18T00:00:00.000000+00:00", "2026-10-18T00:00:00.000000+00:00");

        Pin(
            new MarketPage([market], "QQ"),
            """{"markets":[{"ticker":"A","event_ticker":"E","type":"binary","status":"active","yes_label":"Yes","no_label":"No","open_time":null,"close_time":null,"expiration_time":null,"result":"","settlement_value":null,"settlement_time":null,"first_seen_at":"2026-10-18T00:00:00.000000+00:00","last_seen_at":"2026-10-18T00:00:00.000000+00:00"}],"next_cursor":"QQ"}""");
        Pin(new MarketPage([], null), """{"markets":[],"next_cursor":null}""");
    }

    [Fact]
    public void ATickAndItsPage()
    {
        var tick = new TickDto("2026-10-18T22:23:48.123000+00:00", ApiFormat.Dollars(560000), ApiFormat.Dollars(550000), ApiFormat.Dollars(570000), ApiFormat.Count(1000), ApiFormat.Count(2050), ApiFormat.Count(123400), ApiFormat.Count(9900), null);

        Pin(tick, """{"time":"2026-10-18T22:23:48.123000+00:00","price":"0.560000","yes_bid":"0.550000","yes_ask":"0.570000","yes_bid_size":"10.00","yes_ask_size":"20.50","volume":"1234.00","open_interest":"99.00","last_trade_size":null}""");
        Pin(new TickPage([tick], null), """{"ticks":[{"time":"2026-10-18T22:23:48.123000+00:00","price":"0.560000","yes_bid":"0.550000","yes_ask":"0.570000","yes_bid_size":"10.00","yes_ask_size":"20.50","volume":"1234.00","open_interest":"99.00","last_trade_size":null}],"next_cursor":null}""");
    }

    [Fact]
    public void ATradeAndItsPage()
    {
        var trade = new TradeDto("2026-10-18T22:23:48.123000+00:00", "t-1", "0.400000", "0.600000", "3.00", "yes", false);

        Pin(trade, """{"time":"2026-10-18T22:23:48.123000+00:00","trade_id":"t-1","yes_price":"0.400000","no_price":"0.600000","count":"3.00","taker_side":"yes","is_block_trade":false}""");
        Pin(new TradePage([trade with { TakerSide = null, IsBlockTrade = true }], "Qw"), """{"trades":[{"time":"2026-10-18T22:23:48.123000+00:00","trade_id":"t-1","yes_price":"0.400000","no_price":"0.600000","count":"3.00","taker_side":null,"is_block_trade":true}],"next_cursor":"Qw"}""");
    }

    [Fact]
    public void ACandleAndItsPage()
    {
        var candle = new CandleDto("2026-10-18T22:00:00.000000+00:00", "0.400000", "0.550000", "0.390000", "0.450000", "400.00", 3, "0.440000", "0.460000", "123.00", false, false, false, null, null);

        Pin(candle, """{"time":"2026-10-18T22:00:00.000000+00:00","open":"0.400000","high":"0.550000","low":"0.390000","close":"0.450000","volume":"400.00","trade_count":3,"yes_bid":"0.440000","yes_ask":"0.460000","open_interest":"123.00","partial":false,"trades_partial":false,"book_partial":false,"gap_kind":null,"gap_seconds":null}""");
        Pin(new CandlePage("1h", "stored 1h", [candle with { Open = null, High = null, Low = null, Close = null, Partial = true, BookPartial = true, GapKind = "ws_lost", GapSeconds = 45 }], "Qw"), """{"interval":"1h","source":"stored 1h","candles":[{"time":"2026-10-18T22:00:00.000000+00:00","open":null,"high":null,"low":null,"close":null,"volume":"400.00","trade_count":3,"yes_bid":"0.440000","yes_ask":"0.460000","open_interest":"123.00","partial":true,"trades_partial":false,"book_partial":true,"gap_kind":"ws_lost","gap_seconds":45}],"next_cursor":"Qw"}""");
    }

    [Fact]
    public void TheMetricsOfAMarket()
    {
        var metrics = new MetricsDto("KX-1", "2026-10-18T23:00:00.000000+00:00", "2026-10-18T22:59:59.250000+00:00", "0.560000", "0.020000", "4.00", 2, "99.00", "0.550000", "0.570000", "0.020000", "0.550000", "0.540000", "0.500000", "0.012000", false);

        Pin(metrics, """{"ticker":"KX-1","as_of":"2026-10-18T23:00:00.000000+00:00","last_time":"2026-10-18T22:59:59.250000+00:00","last":"0.560000","change24h":"0.020000","volume24h":"4.00","trade_count24h":2,"open_interest":"99.00","yes_bid":"0.550000","yes_ask":"0.570000","spread":"0.020000","vwap24h":"0.550000","sma20":"0.540000","sma50":"0.500000","volatility24h":"0.012000","partial":false}""");
    }

    [Fact]
    public void AScreenerRowItsPageAndTheAnalyticsStatus()
    {
        var row = new ScreenerRowDto("KX-1", "EV-1", "active", "Yes", "2026-10-18T22:55:00.000000+00:00", "2026-10-18T22:59:00.000000+00:00", "0.560000", "0.020000", "4.00", 2, "99.00", "0.550000", "0.570000", "0.020000", "0.550000", "0.540000", "0.500000", "0.012000", false);

        Pin(row, """{"ticker":"KX-1","event_ticker":"EV-1","status":"active","yes_label":"Yes","changed_at":"2026-10-18T22:55:00.000000+00:00","last_time":"2026-10-18T22:59:00.000000+00:00","last":"0.560000","change24h":"0.020000","volume24h":"4.00","trade_count24h":2,"open_interest":"99.00","yes_bid":"0.550000","yes_ask":"0.570000","spread":"0.020000","vwap24h":"0.550000","sma20":"0.540000","sma50":"0.500000","volatility24h":"0.012000","partial":false}""");
        Pin(new ScreenerPage("volume", "desc", "2026-10-18T22:55:00.000000+00:00", 300, [row with { Last = null, Partial = true }], "Qw"), """{"sort":"volume","order":"desc","refreshed_at":"2026-10-18T22:55:00.000000+00:00","age_seconds":300,"rows":[{"ticker":"KX-1","event_ticker":"EV-1","status":"active","yes_label":"Yes","changed_at":"2026-10-18T22:55:00.000000+00:00","last_time":"2026-10-18T22:59:00.000000+00:00","last":null,"change24h":"0.020000","volume24h":"4.00","trade_count24h":2,"open_interest":"99.00","yes_bid":"0.550000","yes_ask":"0.570000","spread":"0.020000","vwap24h":"0.550000","sma20":"0.540000","sma50":"0.500000","volatility24h":"0.012000","partial":true}],"next_cursor":"Qw"}""");
        Pin(new AnalyticsStatusDto("ok", "5 written", "2026-10-18T22:55:00.000000+00:00", "2026-10-18T22:55:00.000000+00:00", 300, 11, 201000), """{"last":"ok","last_message":"5 written","last_at":"2026-10-18T22:55:00.000000+00:00","last_ok_at":"2026-10-18T22:55:00.000000+00:00","last_ok_age_seconds":300,"last_ok_seconds":11,"last_ok_rows":201000}""");
    }

    [Fact]
    public void AGapAndItsPage()
    {
        var gap = new GapDto(7, "ingest_down", "2026-10-19T04:33:49.000000+00:00", "2026-10-19T04:40:46.000000+00:00", "the previous run stopped cleanly", new BackfillDto("done", 120, 7, "2026-10-19T04:41:30.000000+00:00", "window recovered"));

        Pin(gap, """{"id":7,"kind":"ingest_down","from":"2026-10-19T04:33:49.000000+00:00","to":"2026-10-19T04:40:46.000000+00:00","detail":"the previous run stopped cleanly","backfill":{"status":"done","found":120,"added":7,"at":"2026-10-19T04:41:30.000000+00:00","note":"window recovered"}}""");
        Pin(new GapPage([gap with { To = null, Detail = null }]), """{"gaps":[{"id":7,"kind":"ingest_down","from":"2026-10-19T04:33:49.000000+00:00","to":null,"detail":null,"backfill":{"status":"done","found":120,"added":7,"at":"2026-10-19T04:41:30.000000+00:00","note":"window recovered"}}]}""");
    }

    [Fact]
    public void TheStatusReport()
    {
        var status = new StatusDto(
            "2026-10-18T22:53:13.000000+00:00",
            ["ingest heartbeat is 95 s old"],
            new IngestStateDto("running", "2026-10-18T22:51:38.000000+00:00", 95),
            new StreamsDto(new StreamDto("2026-10-18T22:53:12.500000+00:00", 1), new StreamDto(null, null)),
            new StorageDto(142_606_336, 107_374_182_400, 988_000_000_000, 120_000_000, 0, "2026-10-17", "ok used=0% dropped=", 12),
            new Dictionary<string, long> { ["active"] = 120146, ["unlisted"] = 342 },
            new GapSummaryDto(24, 2, new Dictionary<string, long> { ["ingest_down"] = 2 }, "ticker data in a gap is not recoverable; trades are backfilled", 1, 0),
            new BooksStatusDto(3, 1, 0, 4));

        Pin(
            status,
            """{"time":"2026-10-18T22:53:13.000000+00:00","problems":["ingest heartbeat is 95 s old"],"ingest":{"state":"running","last_heartbeat":"2026-10-18T22:51:38.000000+00:00","heartbeat_age_seconds":95},"streams":{"ticker":{"last_time":"2026-10-18T22:53:12.500000+00:00","age_seconds":1},"trade":{"last_time":null,"age_seconds":null}},"storage":{"data_file_bytes":142606336,"max_bytes":107374182400,"disk_free_bytes":988000000000,"used_bytes":120000000,"used_percent":0,"oldest_data_day":"2026-10-17","governor_last":"ok used=0% dropped=","governor_age_seconds":12},"markets":{"active":120146,"unlisted":342},"gaps":{"window_hours":24,"count":2,"by_kind":{"ingest_down":2},"note":"ticker data in a gap is not recoverable; trades are backfilled","backfill_pending":1,"backfill_failed":0},"books":{"watched":3,"stale":1,"approximate":0,"published_age_seconds":4},"backup":null,"analytics":null}""");
    }

    [Fact]
    public void AnOrderBookAndItsPage()
    {
        var book = new OrderBookDto(
            "KXA-1",
            "2026-10-19T12:00:00.250000+00:00",
            "2026-10-19T12:00:03.000000+00:00",
            2,
            false,
            true,
            [new BookLevelDto("0.410000", "1.50"), new BookLevelDto("0.400000", "5.00")],
            [new BookLevelDto("0.550000", "2.00")]);
        const string Json = """{"ticker":"KXA-1","updated_at":"2026-10-19T12:00:00.250000+00:00","published_at":"2026-10-19T12:00:03.000000+00:00","age_seconds":2,"stale":false,"approximate":true,"yes_bids":[{"price":"0.410000","quantity":"1.50"},{"price":"0.400000","quantity":"5.00"}],"no_bids":[{"price":"0.550000","quantity":"2.00"}]}""";

        Pin(book, Json);
        Pin(new OrderBookPage([book]), "{\"books\":[" + Json + "]}");
        Pin(new OrderBookPage([]), """{"books":[]}""");
    }

    [Fact]
    public void AHealthCheck()
    {
        Pin(new HealthDto("ok"), """{"status":"ok"}""");
    }

    [Fact]
    public void AnError()
    {
        Pin(new ErrorDto("not_found"), """{"error":"not_found","fields":null}""");
        Pin(new ErrorDto("invalid_parameter", ["limit", "cursor"]), """{"error":"invalid_parameter","fields":["limit","cursor"]}""");
    }

    [Fact]
    public void EveryModelThatCrossesTheApiIsRegisteredForSerialization()
    {
        var models = typeof(MarketDto).Assembly.GetExportedTypes()
            .Where(t => t.IsSealed && t.Namespace == "KalTerm.Contracts" && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal) || t.Name.EndsWith("Page", StringComparison.Ordinal))
            .ToList();

        Assert.True(models.Count >= 12);
        foreach (var model in models)
        {
            Assert.True(ApiJson.Wire.GetTypeInfo(model) is not null, $"{model.Name} is not registered in ApiJson");
        }
    }
}
