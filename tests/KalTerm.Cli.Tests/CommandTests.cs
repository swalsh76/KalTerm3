using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;
using static KalTerm.Cli.Tests.Samples;

namespace KalTerm.Cli.Tests;

/// <summary>The real command line parser and the real views, over a fake API and a test console: what is asked, what is printed and what the exit code says.</summary>
public sealed class CommandTests : IDisposable
{
    private readonly Harness _cli = new();

    public void Dispose() => _cli.Dispose();

    // ---- status ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHealthyStatusPrintsEverySectionAndExitsZero()
    {
        var exit = await _cli.RunAsync("status");

        Assert.Equal(CliApp.Ok, exit);
        var output = _cli.Output;
        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", output, StringComparison.Ordinal);
        Assert.Contains("All checks passed.", output, StringComparison.Ordinal);
        Assert.Contains("last heartbeat 3s ago", output, StringComparison.Ordinal);
        Assert.Contains("ticker 1s ago", output, StringComparison.Ordinal);
        Assert.Contains("trade 2s ago", output, StringComparison.Ordinal);
        Assert.Contains("database used 251.8 MB of 100.0 GB (0.2%), file 264.0 MB", output, StringComparison.Ordinal);
        Assert.Contains("oldest raw data 2026-10-17  |  governor ok used=0% dropped= 40s ago", output, StringComparison.Ordinal);
        Assert.Contains("disk free 920.1 GB", output, StringComparison.Ordinal);
        Assert.Contains("active 120,018  |  unlisted 428  |  closed 288", output, StringComparison.Ordinal);
        Assert.Contains("2 in the last 24 h  (ingest_down 2)", output, StringComparison.Ordinal);
        Assert.Contains("Ticker updates in a gap are lost.", output, StringComparison.Ordinal);
        Assert.Empty(_cli.Error);
    }

    [Fact]
    public async Task ProblemsComeFirstAreEscapedAndMakeTheExitCodeTwo()
    {
        _cli.Api.Status = Status("the ingest heartbeat is 95 s old", "detail [red]with brackets[/]");

        var exit = await _cli.RunAsync("status");

        Assert.Equal(CliApp.NeedsAttention, exit);
        Assert.Contains("NEEDS ATTENTION (2)", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("detail [red]with brackets[/]", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("All checks passed.", _cli.Output, StringComparison.Ordinal);
        Assert.True(_cli.Output.IndexOf("NEEDS ATTENTION", StringComparison.Ordinal) < _cli.Output.IndexOf("Ingest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusAsJsonIsTheApisJsonAndKeepsTheExitCode()
    {
        _cli.Api.Status = Status("trouble");

        var exit = await _cli.RunAsync("status", "--json");

        Assert.Equal(CliApp.NeedsAttention, exit);
        Assert.Equal(ApiJson.Serialize(_cli.Api.Status), _cli.Output.Trim());
    }

    [Fact]
    public async Task AStatusWithNoDataAndNoCapStillPrints()
    {
        _cli.Api.Status = Status() with
        {
            Ingest = new IngestStateDto("unknown", null, null),
            Streams = new StreamsDto(new StreamDto(null, null), new StreamDto(null, null)),
            Storage = new StorageDto(1_000, null, null, 900, null, null, null, null),
            Markets = new Dictionary<string, long>(),
            Gaps = new GapSummaryDto(24, 0, new Dictionary<string, long>(), "note"),
        };

        var exit = await _cli.RunAsync("status");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("unknown", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("none in 24 h", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("of no cap", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("Markets   none", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0 in the last 24 h", _cli.Output, StringComparison.Ordinal);
    }

    // ---- markets and market ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MarketsPrintATableAndTellHowToGetTheNextPage()
    {
        _cli.Api.Markets = new MarketPage([Market("A-1"), Market("A-2", "closed")], "QQ");

        var exit = await _cli.RunAsync("markets");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("A-1", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("closed", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2026-10-19 23:00:00", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("EV-A-2", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2 shown. More available: --cursor QQ", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(new MarketsQuery(null, null, null, 25, null), _cli.Api.MarketsQuery);
    }

    [Fact]
    public async Task MarketFiltersAndPagingGoToTheApiUnchanged()
    {
        var exit = await _cli.RunAsync("markets", "--status", "closed", "--event", "EV-1", "-q", "ny", "--limit", "7", "--cursor", "QQ");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(new MarketsQuery("closed", "EV-1", "ny", 7, "QQ"), _cli.Api.MarketsQuery);
        Assert.Contains("1 shown.", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("More available", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneMarketShowsDetailsTheLatestTickAndRecentTrades()
    {
        _cli.Api.Trades = new TradePage([Trade("t-1", "yes"), Trade("t-2", "no", true), Trade("t-3", null)], null);

        var exit = await _cli.RunAsync("market", "A-1");

        Assert.Equal(CliApp.Ok, exit);
        var output = _cli.Output;
        Assert.Contains("A-1", output, StringComparison.Ordinal);
        Assert.Contains("EV-A-1", output, StringComparison.Ordinal);
        Assert.Contains("Yes / No", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 14:00:00 UTC", output, StringComparison.Ordinal);
        Assert.Contains("Latest tick  2026-10-18 22:59:59.250 UTC", output, StringComparison.Ordinal);
        Assert.Contains("price 0.56  bid 0.55 x 10  ask 0.57 x 20.50", output, StringComparison.Ordinal);
        Assert.Contains("volume 1,234,567  open interest 99", output, StringComparison.Ordinal);
        Assert.Contains("Recent trades", output, StringComparison.Ordinal);
        Assert.Contains("block", output, StringComparison.Ordinal);
        Assert.Equal(("A-1", new WindowQuery(Limit: 1)), _cli.Api.TickQueries.Single());
        Assert.Equal(("A-1", new WindowQuery(Limit: 5)), _cli.Api.TradeQueries.Single());
    }

    [Fact]
    public async Task AMarketWithNoTickOrTradesSaysSo()
    {
        _cli.Api.Ticks = new TickPage([], null);
        _cli.Api.Trades = new TradePage([], null);
        _cli.Api.Market = Market("A-1", "finalized") with { Result = "yes", SettlementValue = "1.000000", SettlementTime = "2026-10-19T01:00:00.000000+00:00", OpenTime = null };

        await _cli.RunAsync("market", "A-1");

        Assert.Contains("none recorded", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("1.00 at 2026-10-19 01:00:00 UTC", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("yes", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMarketAsJsonIsJustTheMarketAndMakesNoOtherCalls()
    {
        var exit = await _cli.RunAsync("market", "A-1", "--json");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(ApiJson.Serialize(_cli.Api.Market!), _cli.Output.Trim());
        Assert.Empty(_cli.Api.TickQueries);
        Assert.Empty(_cli.Api.TradeQueries);
    }

    [Fact]
    public async Task AnUnknownMarketIsAShortErrorNotAStackTrace()
    {
        _cli.Api.Market = null;

        var exit = await _cli.RunAsync("market", "NOPE");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("error: not found", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Output);
    }

    // ---- ticks and trades -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TicksSinceAnAgoStartThatLongBeforeNow()
    {
        var exit = await _cli.RunAsync("ticks", "A-1", "--since", "15m", "--limit", "5");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(("A-1", new WindowQuery(Now.UtcDateTime.AddMinutes(-15), null, 5, null)), _cli.Api.TickQueries.Single());
        Assert.Contains("2026-10-18 22:59:59.250", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("1,234,567", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("20.50", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicksFromAnExplicitWindowWithACursor()
    {
        _cli.Api.Ticks = new TickPage([Tick()], "NEXT");

        var exit = await _cli.RunAsync("ticks", "A-1", "--start", "2026-10-18T16:00:00-06:00", "--end", "2026-10-18T23:00:00Z", "--cursor", "C0");

        Assert.Equal(CliApp.Ok, exit);
        var expected = new WindowQuery(new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc), 20, "C0");
        Assert.Equal(("A-1", expected), _cli.Api.TickQueries.Single());
        Assert.Contains("1 shown. More available: --cursor NEXT", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAWindowTicksAskForTheLatest()
    {
        await _cli.RunAsync("ticks", "A-1");

        Assert.Equal(("A-1", new WindowQuery(null, null, 20, null)), _cli.Api.TickQueries.Single());
    }

    [Theory]
    [InlineData("--since", "1h", "--start", "2026-10-18T22:00:00Z", "either --since or --start")]
    [InlineData("--start", "2026-10-18T22:00:00", "", "", "with its offset")]
    [InlineData("--end", "2026-10-18T22:00:00", "", "", "with its offset")]
    [InlineData("--since", "abc", "", "", "for example 15m")]
    [InlineData("--since", "0m", "", "", "for example 15m")]
    public async Task ContradictoryOrUnusableTimeOptionsAreRefusedBeforeAnyCall(string a, string av, string b, string bv, string message)
    {
        var args = new List<string> { "ticks", "A-1", a, av };
        if (b.Length > 0)
        {
            args.AddRange([b, bv]);
        }

        var exit = await _cli.RunAsync([.. args]);

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains(message, _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Api.TickQueries);
    }

    [Fact]
    public async Task CandlesShowThePricesTheVolumeAndMarkBucketsThatOverlapAGap()
    {
        _cli.Api.Candles = new CandlePage("15m", "rolled up from stored 1m", [
            new CandleDto("2026-10-18T22:00:00.000000+00:00", "0.400000", "0.550000", "0.390000", "0.450000", "1,234.00", 3, "0.440000", "0.460000", "123.00", false, false, false, null, null),
            new CandleDto("2026-10-18T22:15:00.000000+00:00", null, null, null, null, "0.00", 0, "0.440000", "0.460000", "123.00", true, true, false, "ingest_down", 90)], "NEXT");

        var exit = await _cli.RunAsync("candles", "A-1", "--interval", "15m", "--since", "6h", "--limit", "2");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(("A-1", "15m", new WindowQuery(Now.UtcDateTime.AddHours(-6), null, 2, null)), _cli.Api.CandleQueries.Single());
        Assert.Contains("0.55", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("partial: trades, ingest_down 90s", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2 shown, 15m candles (rolled up from stored 1m)", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("More available: --cursor NEXT", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandlesDefaultToHourlyAndTheLatestAndAnApiRefusalIsReported()
    {
        await _cli.RunAsync("candles", "A-1");
        Assert.Equal(("A-1", "1h", new WindowQuery(null, null, 30, null)), _cli.Api.CandleQueries.Single());

        _cli.Api.Throw = new ApiException(HttpStatusCode.UnprocessableEntity, "invalid_parameter", ["interval"]);
        var exit = await _cli.RunAsync("candles", "A-1", "--interval", "7s");

        Assert.NotEqual(CliApp.Ok, exit);
    }

    // ---- time zone ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TimesAreShownInTheZoneGivenWithTzAndNameItsOffset()
    {
        var exit = await _cli.RunAsync("status", "--tz", "America/Denver");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("Server status 2026-10-18 17:00:00 -06:00", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATableHeaderNamesTheZoneItsTimesAreIn()
    {
        await _cli.RunAsync("ticks", "A-1", "--tz", "America/Denver");

        Assert.Contains("Time (America/Denver)", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 16:59:59.250", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnyChoiceTimesAreInTheComputersZone()
    {
        _cli.SystemZone = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");

        await _cli.RunAsync("status");

        Assert.Contains("Server status 2026-10-18 17:00:00 -06:00", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVariableBeatsTheComputersZoneAndTheOptionBeatsTheVariable()
    {
        using var viaVariable = new Harness(new Dictionary<string, string> { [CliEnvironment.TimeZoneVariable] = "Asia/Tokyo" }) { SystemZone = TimeZoneInfo.FindSystemTimeZoneById("America/Denver") };
        await viaVariable.RunAsync("status");
        using var viaOption = new Harness(new Dictionary<string, string> { [CliEnvironment.TimeZoneVariable] = "Asia/Tokyo" });
        await viaOption.RunAsync("status", "--tz", "Europe/London");

        Assert.Contains("Server status 2026-10-19 08:00:00 +09:00", viaVariable.Output, StringComparison.Ordinal);
        Assert.Contains("Server status 2026-10-19 00:00:00 +01:00", viaOption.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UtcBeatsEverythingElse()
    {
        using var cli = new Harness(new Dictionary<string, string> { [CliEnvironment.TimeZoneVariable] = "Asia/Tokyo" }) { SystemZone = TimeZoneInfo.FindSystemTimeZoneById("America/Denver") };

        await cli.RunAsync("status", "--tz", "Europe/London", "--utc");

        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZoneNobodyKnowsIsRefusedBeforeAnyCallAndTheMessageSaysWhereItCameFrom()
    {
        var exit = await _cli.RunAsync("status", "--tz", "Mars/Olympus");
        using var fromVariable = new Harness(new Dictionary<string, string> { [CliEnvironment.TimeZoneVariable] = "Nowhere" });
        var exitVariable = await fromVariable.RunAsync("status");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Equal(CliApp.Failed, exitVariable);
        Assert.Equal(0, _cli.ClientsCreated);
        Assert.Contains("not a time zone this computer knows", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("KALTERM_TZ", fromVariable.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonIsNeverShiftedIntoAZone()
    {
        await _cli.RunAsync("status", "--json", "--tz", "Asia/Tokyo");

        Assert.Contains("\"time\":\"2026-10-18T23:00:00.000000+00:00\"", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScreenerListsMarketsAndSaysHowOldTheFiguresAre()
    {
        _cli.Api.Screener = new ScreenerPage("volume", "desc", "2026-10-18T22:55:00.000000+00:00", 300, [
            new ScreenerRowDto("KX-A", "EV-A", "active", "Yes", "2026-10-18T22:55:00.000000+00:00", null, "0.560000", "0.020000", "1234.56", 7, "99.00", "0.550000", "0.570000", "0.020000", null, null, null, "0.012000", false),
            new ScreenerRowDto("KX-B", "EV-B", "closed", "Yes", "2026-10-18T22:55:00.000000+00:00", null, null, "-0.010000", "5.00", 1, null, null, null, null, null, null, null, null, true)], "NEXT");

        var exit = await _cli.RunAsync("screen");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(new ScreenQuery("volume", "desc", null, null, 20, null), _cli.Api.ScreenQueries.Single());
        Assert.Contains("KX-A", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("+0.02", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("-0.01", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("1,234.56", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("partial", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2 shown, ranked by volume (desc); figures from 2026-10-18 22:55:00 UTC, 5m ago.", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("More available: --cursor NEXT", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenOptionsReachTheApiAndAScreenerWithoutARunSaysSo()
    {
        _cli.Api.Screener = new ScreenerPage("spread", "asc", null, null, [], null);

        await _cli.RunAsync("screen", "--sort", "spread", "--asc", "--status", "active", "--min-volume", "1000", "--top", "5", "--cursor", "C0");

        Assert.Equal(new ScreenQuery("spread", "asc", "active", "1000", 5, "C0"), _cli.Api.ScreenQueries.Single());
        Assert.Contains("the metrics analyzer has not finished a run yet", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenSearchReachesTheApi()
    {
        _cli.Api.Screener = new ScreenerPage("volume", "desc", null, null, [], null);

        await _cli.RunAsync("screen", "--search", "wash");

        Assert.Equal("wash", _cli.Api.ScreenQueries.Single().Search);
    }

    [Fact]
    public async Task TheStatusShowsTheAnalyticsLine()
    {
        _cli.Api.Status = Samples.Status() with { Analytics = new AnalyticsStatusDto("ok", "x", "2026-10-18T22:55:00.000000+00:00", "2026-10-18T22:55:00.000000+00:00", 300, 11, 201_000) };

        await _cli.RunAsync("status");

        Assert.Contains("Analytics", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("screener figures 5m ago, 201,000 markets in 11 s", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAnalyzerRunIsShownInTheStatusLine()
    {
        _cli.Api.Status = Samples.Status("the metrics analyzer's last run failed: no database") with { Analytics = new AnalyticsStatusDto("failed", "no database", "2026-10-18T22:55:00.000000+00:00", null, null, null, null) };

        await _cli.RunAsync("status");

        Assert.Contains("last run failed: no database", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("no successful run yet", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsShowEveryFigureAndASignedChange()
    {
        var exit = await _cli.RunAsync("metrics", "A-1");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("change 24h +0.02", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("VWAP 0.555", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("volume 24h 1,234 in 7 trades", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsOfAMarketWithGapsOrNoDataSaySoAndTimesFollowTheZone()
    {
        _cli.Api.Metrics = _cli.Api.Metrics with { Partial = true, LastTime = null, Last = null, Change24h = "-0.010000", Vwap24h = null };

        await _cli.RunAsync("metrics", "A-1", "--tz", "America/Denver");

        Assert.Contains("change 24h -0.01", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("no ticker update in the last 7 days", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("partial: a gap overlaps", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("as of 2026-10-18 17:00:00 -06:00", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TradesShowBothPricesTheTakerAndBlocks()
    {
        _cli.Api.Trades = new TradePage([Trade("t-1", "yes"), Trade("t-2", "no", true)], null);

        var exit = await _cli.RunAsync("trades", "A-1", "--since", "2h");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(("A-1", new WindowQuery(Now.UtcDateTime.AddHours(-2), null, 20, null)), _cli.Api.TradeQueries.Single());
        Assert.Contains("0.40", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.60", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("block", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("2 shown.", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TradesRefuseTheSameBadTimeOptions()
    {
        var exit = await _cli.RunAsync("trades", "A-1", "--since", "1h", "--start", "2026-10-18T22:00:00Z");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Empty(_cli.Api.TradeQueries);
    }

    [Fact]
    public async Task TicksAndTradesAsJsonPrintTheApisPages()
    {
        await _cli.RunAsync("ticks", "A-1", "--json");
        var ticks = _cli.Output.Trim();
        using var second = new Harness();
        await second.RunAsync("trades", "A-1", "--json");

        Assert.Equal(ApiJson.Serialize(_cli.Api.Ticks), ticks);
        Assert.Equal(ApiJson.Serialize(second.Api.Trades), second.Output.Trim());
    }

    // ---- gaps -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GapsShowOpenAndClosedOnesWithTheirLengthAndTheLookbackDefaultsToADay()
    {
        _cli.Api.Gaps = new GapPage(
        [
            new GapDto(2, "ws_lost", "2026-10-18T22:50:00.000000+00:00", null, "still open [x]"),
            new GapDto(1, "ingest_down", "2026-10-19T04:33:49.000000+00:00", "2026-10-19T04:40:46.000000+00:00", "the previous run stopped cleanly"),
        ]);

        var exit = await _cli.RunAsync("gaps");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(new WindowQuery(Now.UtcDateTime.AddHours(-24), null, 50, null), _cli.Api.GapQuery);
        Assert.Contains("open", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("10m 0s+", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("6m 57s", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("still open [x]", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("Data in a gap is lost", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GapsSinceALongerTimeAndABadOne()
    {
        var exit = await _cli.RunAsync("gaps", "--since", "7d", "--limit", "3");
        var bad = await _cli.RunAsync("gaps", "--since", "forever");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(CliApp.Failed, bad);
        Assert.Equal(new WindowQuery(Now.UtcDateTime.AddDays(-7), null, 3, null), _cli.Api.GapQuery);
        Assert.Contains("for example 30m", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GapsAsJsonArePrintedAsTheApiSentThem()
    {
        _cli.Api.Gaps = new GapPage([new GapDto(1, "ws_lost", "2026-10-18T22:50:00.000000+00:00", null, null)]);

        await _cli.RunAsync("gaps", "--json");

        Assert.Equal(ApiJson.Serialize(_cli.Api.Gaps), _cli.Output.Trim());
    }

    // ---- failures -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnApiThatCannotBeReachedIsOneLineAndExitCodeOne()
    {
        _cli.Api.Throw = new ApiUnavailableException("could not reach the API at http://127.0.0.1:5280/: connection refused");

        var exit = await _cli.RunAsync("status");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("error: could not reach the API at http://127.0.0.1:5280/: connection refused", _cli.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Output);
    }

    [Fact]
    public async Task ARefusalNamesTheParametersTheApiObjectedTo()
    {
        _cli.Api.Throw = new ApiException(HttpStatusCode.UnprocessableEntity, "invalid_parameter", ["limit"]);

        var exit = await _cli.RunAsync("markets", "--limit", "0");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("check limit", _cli.Error, StringComparison.Ordinal);
    }

    // ---- where it talks and how it prints -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheApiAddressIsTheOptionThenTheVariableThenTheDefaultAndTheClientIsDisposed()
    {
        await _cli.RunAsync("status");
        Assert.Equal("http://127.0.0.1:5280/", _cli.ApiAddress!.AbsoluteUri);
        Assert.True(_cli.Api.Disposed);

        _cli.Variables[CliEnvironment.ApiVariable] = "http://10.0.0.5:9000";
        await _cli.RunAsync("status");
        Assert.Equal("http://10.0.0.5:9000/", _cli.ApiAddress.AbsoluteUri);

        await _cli.RunAsync("status", "--api", "http://127.0.0.1:6000");
        Assert.Equal("http://127.0.0.1:6000/", _cli.ApiAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://host/")]
    [InlineData("not a url")]
    [InlineData("127.0.0.1:5280")]
    public async Task AnAddressThatIsNotHttpIsRefusedWithoutCreatingAClient(string address)
    {
        var exit = await _cli.RunAsync("status", "--api", address);

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("is not an http or https address", _cli.Error, StringComparison.Ordinal);
        Assert.Equal(0, _cli.ClientsCreated);
    }

    [Fact]
    public async Task WidthIsSetAndClampedAndNoColorLeavesNoEscapeCodes()
    {
        await _cli.RunAsync("status", "--width", "100");
        Assert.Equal(100, _cli.Width);

        await _cli.RunAsync("status", "--width", "10");
        Assert.Equal(40, _cli.Width);

        var exit = await _cli.RunAsync("status", "--no-color");
        Assert.Equal(CliApp.Ok, exit);
        Assert.DoesNotContain('\u001b', _cli.Output);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("ticks")]
    [InlineData("market")]
    [InlineData("markets", "--limit", "many")]
    [InlineData("status", "--no-such-option")]
    public async Task ACommandLineThatDoesNotParseIsAnErrorWithoutTouchingTheApi(params string[] args)
    {
        var exit = await _cli.RunAsync(args);

        Assert.NotEqual(CliApp.Ok, exit);
        Assert.NotEmpty(_cli.Error);
        Assert.Null(_cli.Api.MarketsQuery);
    }

    [Fact]
    public async Task HelpListsEveryCommand()
    {
        var exit = await _cli.RunAsync("--help");

        Assert.Equal(CliApp.Ok, exit);
        foreach (var command in new[] { "status", "markets", "market", "ticks", "trades", "gaps", "--api", "--json" })
        {
            Assert.Contains(command, _cli.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("markets")]
    [InlineData("market", "A-1")]
    [InlineData("ticks", "A-1")]
    [InlineData("trades", "A-1")]
    [InlineData("gaps")]
    public async Task EveryCommandPrintsPlainAsciiSoItSurvivesRemotingAndPipes(params string[] args)
    {
        _cli.Api.Trades = new TradePage([Trade("t-1", "yes"), Trade("t-2", "no", true)], null);
        _cli.Api.Gaps = new GapPage([new GapDto(1, "ws_lost", "2026-10-18T22:50:00.000000+00:00", null, "plain detail")]);

        var exit = await _cli.RunAsync(args);

        Assert.Equal(CliApp.Ok, exit);
        Assert.NotEmpty(_cli.Output);
        Assert.All(_cli.Output, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the output"));
    }

    [Fact]
    public async Task AGapWithALongDetailOnlyWrapsTheDetailAndKeepsTheOtherColumnsWhole()
    {
        _cli.Api.Gaps = new GapPage(
        [
            new GapDto(12, "ws_reconnected", "2026-10-19T04:33:49.000000+00:00", "2026-10-19T04:40:46.000000+00:00", "the previous run did not stop cleanly; from its last heartbeat, and then a lot more words to make this long"),
            new GapDto(3, "ingest_down", "2026-10-18T22:50:00.000000+00:00", null, "short"),
        ]);

        await _cli.RunAsync("gaps", "--width", "100");

        var output = _cli.Output;
        Assert.Contains("ws_reconnected", output, StringComparison.Ordinal);
        Assert.Contains("ingest_down", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-19 04:33:49", output, StringComparison.Ordinal);
        Assert.Contains("6m 57s", output, StringComparison.Ordinal);
        Assert.Contains("10m 0s+", output, StringComparison.Ordinal);
        Assert.Contains("Length", output, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s*12\s+ws_reconnected\s+2026-10-19 04:33:49", output);
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 100, $"line wider than the console: {line}"));
    }

    [Fact]
    public async Task OnANarrowConsoleTheGapDetailMovesUnderItsRowAndNothingIsSqueezed()
    {
        _cli.Api.Gaps = new GapPage([new GapDto(12, "ws_reconnected", "2026-10-19T04:33:49.000000+00:00", "2026-10-19T04:40:46.000000+00:00", "the previous run did not stop cleanly; from its last heartbeat")]);

        await _cli.RunAsync("gaps", "--width", "80");

        var lines = _cli.Output.Split('\n');
        Assert.Contains(lines, line => line.Contains("12  ws_reconnected  2026-10-19 04:33:49  2026-10-19 04:40:46  6m 57s", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.TrimStart().StartsWith("the previous run did not stop cleanly;", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(line.TrimEnd('\r').Length <= 80, $"line wider than the console: {line}"));
    }

    // ---- book -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStatusShowsTheBooksLine()
    {
        await _cli.RunAsync("status");
        Assert.Contains("2 watched, none stale, published 3s ago", _cli.Output, StringComparison.Ordinal);

        using var second = new Harness();
        second.Api.Status = Status() with { Books = new BooksStatusDto(3, 1, 2, 70) };
        await second.RunAsync("status");

        Assert.Contains("3 watched, 1 stale, 2 approximate, published 70s ago", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookWithATickerPrintsTheLadderWithDerivedAsksAndTheSpread()
    {
        var exit = await _cli.RunAsync("book", "A-1", "--depth", "4");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal([("A-1", (int?)4)], _cli.Api.BookQueries);
        var lines = _cli.Output.Split('\n');
        int Line(string text) => Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal));
        // The no bids 0.55 and 0.54 become yes asks 0.45 and 0.46; the lowest ask sits next to the spread, above the bids.
        Assert.True(Line("0.46") < Line("0.45"));
        Assert.True(Line("0.45") < Line("spread 0.04"));
        Assert.True(Line("spread 0.04") < Line("0.41"));
        Assert.True(Line("0.41") < Line("0.40"));
        Assert.Contains("1,500", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("live", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("published 2s ago", _cli.Output, StringComparison.Ordinal);
        Assert.Empty(_cli.Error);
    }

    [Theory]
    [InlineData(true, false, "STALE", "do not trust these levels")]
    [InlineData(false, true, "APPROXIMATE", "may be slightly off")]
    public async Task AStaleOrApproximateBookSaysSo(bool stale, bool approximate, string flag, string warning)
    {
        _cli.Api.Book = Book("A-1", stale, approximate);

        await _cli.RunAsync("book", "A-1");

        Assert.Contains(flag, _cli.Output, StringComparison.Ordinal);
        Assert.Contains(warning, _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookWithoutATickerSummarisesEveryWatchedMarket()
    {
        _cli.Api.Books = new OrderBookPage([Book("A-1"), Book("B-2", stale: true), Book("C-3", approximate: true), Book("D-4") with { YesBids = [], NoBids = [] }]);

        var exit = await _cli.RunAsync("book");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal([(int?)1], _cli.Api.BooksDepths);
        Assert.Contains("A-1", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.41", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.45", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.04", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("STALE", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("APPROX", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("4 watched", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookWithNothingWatchedSaysHowToWatchSomething()
    {
        _cli.Api.Books = new OrderBookPage([]);

        var exit = await _cli.RunAsync("book");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("No markets are watched", _cli.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("book", "A-1")]
    [InlineData("book")]
    public async Task BookAsJsonIsTheApisJson(params string[] args)
    {
        var exit = await _cli.RunAsync([.. args, "--json"]);

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(args.Length == 2 ? ApiJson.Serialize(_cli.Api.Book!) : ApiJson.Serialize(_cli.Api.Books), _cli.Output.Trim());
    }

    [Fact]
    public async Task AnUnwatchedMarketIsReportedAsNotFound()
    {
        _cli.Api.Book = null;

        var exit = await _cli.RunAsync("book", "NOPE-1");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("not found", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGovernorErrorIsShownInRedAndAGovernorThatNeverRanIsSaid()
    {
        _cli.Api.Status = Status() with { Storage = Status().Storage with { GovernorLast = "error used=95% nothing more can be dropped", GovernorAgeSeconds = 5 } };
        await _cli.RunAsync("status");
        Assert.Contains("governor error used=95% nothing more can be dropped 5s ago", _cli.Output, StringComparison.Ordinal);

        using var second = new Harness();
        second.Api.Status = Status() with { Storage = Status().Storage with { GovernorLast = null, GovernorAgeSeconds = null, OldestDataDay = null } };
        await second.RunAsync("status");
        Assert.Contains("oldest raw data -  |  governor has not run", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GapsShowHowTheirTradesWereRecovered()
    {
        _cli.Api.Gaps = new GapPage(
        [
            new GapDto(4, "ingest_down", "2026-10-18T22:50:00.000000+00:00", "2026-10-18T22:55:00.000000+00:00", null, new BackfillDto("done", 12_345, 120, "2026-10-18T22:56:00.000000+00:00", "n")),
            new GapDto(3, "ws_reconnected", "2026-10-18T22:40:00.000000+00:00", "2026-10-18T22:41:00.000000+00:00", null, new BackfillDto("failed", null, null, null, "REST was down")),
            new GapDto(2, "ws_reconnected", "2026-10-18T22:30:00.000000+00:00", "2026-10-18T22:31:00.000000+00:00", null, new BackfillDto("pending", null, null, null, null)),
            new GapDto(1, "ws_lost", "2026-10-18T22:20:00.000000+00:00", "2026-10-18T22:21:00.000000+00:00", null, null),
        ]);

        await _cli.RunAsync("gaps");

        Assert.Contains("recovered 120 of 12,345", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("FAILED", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("pending", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusGapsLineCountsWaitingAndFailedBackfills()
    {
        _cli.Api.Status = Status() with { Gaps = new GapSummaryDto(24, 2, new Dictionary<string, long> { ["ingest_down"] = 2 }, "note", 2, 1) };

        await _cli.RunAsync("status");

        Assert.Contains("trade backfill: 2 waiting, 1 failed", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusBackupLineSaysNotConfiguredOrShowsTheLastGoodBackupAndAnyFailure()
    {
        await _cli.RunAsync("status");
        Assert.Contains("Backup    not configured", _cli.Output, StringComparison.Ordinal);

        using var good = new Harness();
        good.Api.Status = Status() with { Backup = new BackupStatusDto(true, "kalterm-full-20261019-080000.bak", "2026-10-19T08:00:00.000000+00:00", 7200, "ok full", "2026-10-19T08:00:00.000000+00:00", "ok restored", "2026-10-19T08:05:00.000000+00:00") };
        await good.RunAsync("status");
        Assert.Contains("last good kalterm-full-20261019-080000.bak, 2h ago  |  last proof by restore ok", good.Output, StringComparison.Ordinal);

        using var failing = new Harness();
        failing.Api.Status = Status() with { Backup = new BackupStatusDto(true, null, null, null, "error the share is gone", "2026-10-19T09:00:00.000000+00:00", "error bad pages", "2026-10-19T08:05:00.000000+00:00") };
        await failing.RunAsync("status");
        Assert.Contains("no finished backup yet", failing.Output, StringComparison.Ordinal);
        Assert.Contains("last attempt failed: error the share is gone", failing.Output, StringComparison.Ordinal);
        Assert.Contains("proof failed: error bad pages", failing.Output, StringComparison.Ordinal);
    }
}
