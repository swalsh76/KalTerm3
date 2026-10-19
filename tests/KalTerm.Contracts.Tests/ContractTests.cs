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
    public void AGapAndItsPage()
    {
        var gap = new GapDto(7, "ingest_down", "2026-10-19T04:33:49.000000+00:00", "2026-10-19T04:40:46.000000+00:00", "the previous run stopped cleanly");

        Pin(gap, """{"id":7,"kind":"ingest_down","from":"2026-10-19T04:33:49.000000+00:00","to":"2026-10-19T04:40:46.000000+00:00","detail":"the previous run stopped cleanly"}""");
        Pin(new GapPage([gap with { To = null, Detail = null }]), """{"gaps":[{"id":7,"kind":"ingest_down","from":"2026-10-19T04:33:49.000000+00:00","to":null,"detail":null}]}""");
    }

    [Fact]
    public void TheStatusReport()
    {
        var status = new StatusDto(
            "2026-10-18T22:53:13.000000+00:00",
            ["ingest heartbeat is 95 s old"],
            new IngestStateDto("running", "2026-10-18T22:51:38.000000+00:00", 95),
            new StreamsDto(new StreamDto("2026-10-18T22:53:12.500000+00:00", 1), new StreamDto(null, null)),
            new StorageDto(142_606_336, 107_374_182_400, 988_000_000_000),
            new Dictionary<string, long> { ["active"] = 120146, ["unlisted"] = 342 },
            new GapSummaryDto(24, 2, new Dictionary<string, long> { ["ingest_down"] = 2 }, "ticker data in a gap is not recoverable; trades are backfilled"));

        Pin(
            status,
            """{"time":"2026-10-18T22:53:13.000000+00:00","problems":["ingest heartbeat is 95 s old"],"ingest":{"state":"running","last_heartbeat":"2026-10-18T22:51:38.000000+00:00","heartbeat_age_seconds":95},"streams":{"ticker":{"last_time":"2026-10-18T22:53:12.500000+00:00","age_seconds":1},"trade":{"last_time":null,"age_seconds":null}},"storage":{"data_file_bytes":142606336,"max_bytes":107374182400,"disk_free_bytes":988000000000},"markets":{"active":120146,"unlisted":342},"gaps":{"window_hours":24,"count":2,"by_kind":{"ingest_down":2},"note":"ticker data in a gap is not recoverable; trades are backfilled"}}""");
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
