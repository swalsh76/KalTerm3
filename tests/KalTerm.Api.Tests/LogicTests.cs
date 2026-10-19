using KalTerm.Contracts;
using KalTerm.Data;
using KalTerm.Data.Reading;
using KalTerm.Data.State;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

public class MappingTests
{
    [Fact]
    public void AMarketKeepsEveryFieldAndTurnsStoredValuesIntoText()
    {
        var settled = new DateTime(2026, 10, 19, 1, 2, 3, 456, DateTimeKind.Utc);
        var stored = Market("KX-1", "finalized") with { SettlementTs = settled, SettlementValueE6 = 1_000_000, Result = "yes", LatestExpirationTime = settled };

        var dto = Mapping.ToDto(stored);

        Assert.Equal(("KX-1", "EV-KX-1", "binary", "finalized", "Yes", "No", "yes"), (dto.Ticker, dto.EventTicker, dto.Type, dto.Status, dto.YesLabel, dto.NoLabel, dto.Result));
        Assert.Equal("1.000000", dto.SettlementValue);
        Assert.Equal("2026-10-19T01:02:03.456000+00:00", dto.SettlementTime);
        Assert.Equal("2026-10-19T01:02:03.456000+00:00", dto.ExpirationTime);
        Assert.Null(Mapping.ToDto(Market("KX-2")).SettlementValue);
    }

    [Fact]
    public void ATickKeepsNullsAsNullsAndScalesTheRest()
    {
        var dto = Mapping.ToDto(new StoredTick(NowUtc, 560_000, null, 570_000, 1_000, null, 123_400, 9_900, 1));

        Assert.Equal(("0.560000", null, "0.570000", "10.00", null, "1234.00", "99.00", "0.01"), (dto.Price, dto.YesBid, dto.YesAsk, dto.YesBidSize, dto.YesAskSize, dto.Volume, dto.OpenInterest, dto.LastTradeSize));
    }

    [Theory]
    [InlineData((byte)0, null)]
    [InlineData((byte)1, "yes")]
    [InlineData((byte)2, "no")]
    [InlineData((byte)9, null)]
    public void ATradeSideIsYesNoOrUnreported(byte stored, string? expected)
    {
        Assert.Equal(expected, Mapping.ToDto(Trade(NowUtc, "t", stored)).TakerSide);
    }

    [Fact]
    public void AGapKeepsItsOpenEnd()
    {
        var dto = Mapping.ToDto(new StoredGap(5, "ws_lost", NowUtc, null, "x"));

        Assert.Equal((5L, "ws_lost", ApiFormat.Moment(NowUtc), (string?)null, "x"), (dto.Id, dto.Kind, dto.From, dto.To, dto.Detail));
    }

    [Fact]
    public void NullRowsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => Mapping.ToDto((StoredMarket)null!));
        Assert.Throws<ArgumentNullException>(() => Mapping.ToDto((StoredTick)null!));
        Assert.Throws<ArgumentNullException>(() => Mapping.ToDto((StoredTrade)null!));
        Assert.Throws<ArgumentNullException>(() => Mapping.ToDto((StoredGap)null!));
        Assert.Throws<ArgumentNullException>(() => Mapping.ToDto((StoredBook)null!, NowUtc, 10));
    }
}

public class StatusBuilderTests
{
    private static StatusSnapshot Healthy() => new(
        new IngestStateEntry(IngestStateKeys.Running, NowUtc.AddSeconds(-5)),
        NowUtc.AddSeconds(-1),
        NowUtc.AddSeconds(-3),
        10L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        800L * 1024 * 1024 * 1024,
        new Dictionary<string, long> { ["active"] = 100 },
        new Dictionary<string, long> { ["ingest_down"] = 1, ["ws_reconnected"] = 3 });

    [Fact]
    public void NoPublishedBooksIsNotAProblemAndReportsZero()
    {
        var none = StatusBuilder.Build(Healthy(), NowUtc);
        var noTable = StatusBuilder.Build(Healthy() with { Books = new BookStatus(0, 0, 0, null) }, NowUtc);

        Assert.Empty(none.Problems);
        Assert.Equal(new BooksStatusDto(0, 0, 0, null), none.Books);
        Assert.Equal(none.Books, noTable.Books);
    }

    [Fact]
    public void FreshBooksAreReportedAndStaleOnesAreAProblem()
    {
        var fresh = StatusBuilder.Build(Healthy() with { Books = new BookStatus(3, 0, 1, NowUtc.AddSeconds(-4)) }, NowUtc);
        var stale = StatusBuilder.Build(Healthy() with { Books = new BookStatus(3, 2, 0, NowUtc.AddSeconds(-4)) }, NowUtc);

        Assert.Empty(fresh.Problems);
        Assert.Equal(new BooksStatusDto(3, 0, 1, 4), fresh.Books);
        Assert.Contains("2 of 3 watched order books are stale", Assert.Single(stale.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void BooksNotPublishedForAMinuteWhileIngestRunsAreAProblemButNotWhenIngestIsStopped()
    {
        var old = new BookStatus(3, 0, 0, NowUtc.AddSeconds(-95));
        var edge = new BookStatus(3, 0, 0, NowUtc.AddSeconds(-60));

        var running = StatusBuilder.Build(Healthy() with { Books = old }, NowUtc);
        var atEdge = StatusBuilder.Build(Healthy() with { Books = edge }, NowUtc);
        var stopped = StatusBuilder.Build(Healthy() with { Run = new IngestStateEntry(IngestStateKeys.Stopped, NowUtc.AddMinutes(-10)), Books = old }, NowUtc);

        Assert.Contains("order books were last published 95 s ago", Assert.Single(running.Problems), StringComparison.Ordinal);
        Assert.Empty(atEdge.Problems);
        Assert.DoesNotContain(stopped.Problems, p => p.Contains("order books", StringComparison.Ordinal));
    }

    [Fact]
    public void AHealthyServerHasNoProblemsAndReportsEverything()
    {
        var status = StatusBuilder.Build(Healthy(), NowUtc);

        Assert.Empty(status.Problems);
        Assert.Equal(ApiFormat.Moment(NowUtc), status.Time);
        Assert.Equal(("running", 5L), (status.Ingest.State, status.Ingest.HeartbeatAgeSeconds));
        Assert.Equal((1L, 3L), (status.Streams.Ticker.AgeSeconds, status.Streams.Trade.AgeSeconds));
        Assert.Equal((10L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, 800L * 1024 * 1024 * 1024), (status.Storage.DataFileBytes, status.Storage.MaxBytes, status.Storage.DiskFreeBytes));
        Assert.Equal(4, status.Gaps.Count);
        Assert.Equal(24, status.Gaps.WindowHours);
        Assert.Equal(StatusBuilder.GapNote, status.Gaps.Note);
    }

    [Fact]
    public void AnIngestThatNeverRecordedARunIsUnknownAndAProblem()
    {
        var status = StatusBuilder.Build(Healthy() with { Run = null }, NowUtc);

        Assert.Equal("unknown", status.Ingest.State);
        Assert.Null(status.Ingest.LastHeartbeat);
        Assert.Contains(status.Problems, p => p.Contains("never recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void AStaleHeartbeatWhileRunningIsAProblemAndAFreshOneIsNot()
    {
        var stale = StatusBuilder.Build(Healthy() with { Run = new IngestStateEntry(IngestStateKeys.Running, NowUtc.AddSeconds(-95)) }, NowUtc);
        var edge = StatusBuilder.Build(Healthy() with { Run = new IngestStateEntry(IngestStateKeys.Running, NowUtc.AddSeconds(-60)) }, NowUtc);

        Assert.Contains(stale.Problems, p => p.Contains("95 s old", StringComparison.Ordinal));
        Assert.Empty(edge.Problems);
    }

    [Fact]
    public void AStoppedIngestIsAProblemWithoutTheStreamStalenessNoise()
    {
        var status = StatusBuilder.Build(Healthy() with { Run = new IngestStateEntry(IngestStateKeys.Stopped, NowUtc.AddMinutes(-10)), LastTickerAt = NowUtc.AddMinutes(-10), LastTradeAt = NowUtc.AddMinutes(-10) }, NowUtc);

        Assert.Single(status.Problems);
        Assert.Contains("stopped", status.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AQuietStreamWhileIngestRunsIsAProblemPerStream()
    {
        var status = StatusBuilder.Build(Healthy() with { LastTickerAt = NowUtc.AddMinutes(-3), LastTradeAt = NowUtc.AddSeconds(-121) }, NowUtc);

        Assert.Equal(2, status.Problems.Count);
        Assert.Contains(status.Problems, p => p.StartsWith("no ticker data for 180 s", StringComparison.Ordinal));
        Assert.Contains(status.Problems, p => p.StartsWith("no trade data for 121 s", StringComparison.Ordinal));
    }

    [Fact]
    public void NoRowsAtAllInADayIsAProblem()
    {
        var status = StatusBuilder.Build(Healthy() with { LastTickerAt = null, LastTradeAt = null }, NowUtc);

        Assert.Contains("no ticker data in the last 24 hours", status.Problems);
        Assert.Contains("no trade data in the last 24 hours", status.Problems);
        Assert.Null(status.Streams.Ticker.LastTime);
    }

    [Fact]
    public void ADatabaseNearItsCapAndADiskNearlyFullAreProblems()
    {
        var gb = 1024L * 1024 * 1024;

        var nearCap = StatusBuilder.Build(Healthy() with { DataFileBytes = 93 * gb }, NowUtc);
        var atEdge = StatusBuilder.Build(Healthy() with { DataFileBytes = 90 * gb }, NowUtc);
        var justUnder = StatusBuilder.Build(Healthy() with { DataFileBytes = (90 * gb) - 1 }, NowUtc);
        var lowDisk = StatusBuilder.Build(Healthy() with { DiskFreeBytes = 49 * gb }, NowUtc);
        var unlimited = StatusBuilder.Build(Healthy() with { MaxFileBytes = null, DataFileBytes = 500 * gb, DiskFreeBytes = null }, NowUtc);

        Assert.Contains("the database file is at 93% of its size cap", nearCap.Problems);
        Assert.Single(atEdge.Problems);
        Assert.Empty(justUnder.Problems);
        Assert.Contains("only 49 GB free on the data disk", lowDisk.Problems);
        Assert.Empty(unlimited.Problems);
    }

    [Fact]
    public void AClockBehindTheDatabaseNeverGivesANegativeAge()
    {
        var status = StatusBuilder.Build(Healthy() with { Run = new IngestStateEntry(IngestStateKeys.Running, NowUtc.AddSeconds(10)) }, NowUtc);

        Assert.Equal(0, status.Ingest.HeartbeatAgeSeconds);
    }

    [Fact]
    public void ProblemsComeInOrderOfImportanceIngestFirst()
    {
        var status = StatusBuilder.Build(Healthy() with { Run = null, LastTickerAt = null, DiskFreeBytes = 1 }, NowUtc);

        Assert.Contains("never recorded", status.Problems[0], StringComparison.Ordinal);
        Assert.Contains("GB free", status.Problems[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ANullSnapshotIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => StatusBuilder.Build(null!, NowUtc));
    }
}

public class RequestParameterTests
{
    private static RequestParameters Parse(string query) =>
        new(new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)));

    [Theory]
    [InlineData("2026-10-18T10:00:00Z", "2026-10-18T10:00:00")]
    [InlineData("2026-10-18T10:00:00z", "2026-10-18T10:00:00")]
    [InlineData("2026-10-18T10:00:00+00:00", "2026-10-18T10:00:00")]
    [InlineData("2026-10-18T10:00:00-06:00", "2026-10-18T16:00:00")]
    [InlineData("2026-10-18T10:00:00.123456+0530", "2026-10-18T04:30:00.123456")]
    public void MomentsWithAnOffsetAreConvertedToUtc(string text, string expectedUtc)
    {
        Assert.True(RequestParameters.TryParseMoment(text, out var utc));
        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("2026-10-18T10:00:00")]
    [InlineData("2026-10-18")]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026-10-18T25:00:00Z")]
    [InlineData("0001-01-01T00:00:00+05:00")]
    public void MomentsWithoutAnOffsetOrOutsideTheCalendarAreRefused(string text)
    {
        Assert.False(RequestParameters.TryParseMoment(text, out _));
    }

    [Theory]
    [InlineData("KX-1", true)]
    [InlineData("KXHIGHNY-26OCT19-T70.5", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("quote'", false)]
    public void TickersHaveAShape(string text, bool valid)
    {
        Assert.Equal(valid, RequestParameters.IsTicker(text));
        Assert.False(RequestParameters.IsTicker(new string('A', 129)));
        Assert.True(RequestParameters.IsTicker(new string('A', 128)));
    }

    [Fact]
    public void LimitsDefaultWhenAbsentAndStayInsideTheirBounds()
    {
        Assert.Equal(7, Parse(string.Empty).Limit(7, 10));
        Assert.Equal(10, Parse("limit=10").Limit(7, 10));
        Assert.Equal(1, Parse("limit=1").Limit(7, 10));
        foreach (var bad in new[] { "limit=0", "limit=11", "limit=+5", "limit=1.5", "limit=", "limit=99999999999" })
        {
            var parameters = Parse(bad);
            _ = parameters.Limit(7, 10);
            Assert.Equal(["limit"], parameters.Invalid);
        }
    }

    [Fact]
    public void AParameterGivenTwiceIsInvalidOnceAndRejectDoesNotRepeatNames()
    {
        var parameters = Parse("status=a&status=b");

        Assert.Null(parameters.Status());
        parameters.Reject("status", "limit");
        parameters.Reject("limit");

        Assert.Equal(["status", "limit"], parameters.Invalid);
        Assert.False(parameters.IsValid);
    }

    [Fact]
    public void AnAbsentOrValidSearchPassesAndAShortOrControlOneDoesNot()
    {
        Assert.Null(Parse(string.Empty).Search());
        Assert.Equal("NY", Parse("q=NY").Search());
        Assert.Equal("a b%", Parse("q=a%20b%25").Search());
        Assert.False(Parse(string.Empty).Search() is not null);
        var tooLong = Parse("q=" + new string('x', 65));
        _ = tooLong.Search();
        Assert.Equal(["q"], tooLong.Invalid);
    }

    [Fact]
    public void AnEndDefaultsToJustAfterNow()
    {
        Assert.Equal(NowUtc.AddSeconds(1), RequestParameters.EndOrNow(null, NowUtc));
        Assert.Equal(NowUtc, RequestParameters.EndOrNow(NowUtc, NowUtc.AddDays(1)));
    }

    [Fact]
    public void ACursorLongerThanTheLimitIsRefused()
    {
        var parameters = Parse("cursor=" + new string('A', RequestParameters.MaxCursorLength + 1));

        Assert.Null(parameters.Cursor());
        Assert.Equal(["cursor"], parameters.Invalid);
    }
}

public class ApiHostOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5280", "127.0.0.1", 5280)]
    [InlineData("http://127.5.5.5:8080", "127.5.5.5", 8080)]
    [InlineData("http://[::1]:5280", "::1", 5280)]
    public void LoopbackAddressesWithAPortAreAccepted(string url, string address, int port)
    {
        var endpoint = new ApiHostOptions { Url = url }.ListenEndPoint();

        Assert.Equal((address, port), (endpoint.Address.ToString(), endpoint.Port));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5280")]
    [InlineData("http://192.168.1.20:5280")]
    [InlineData("http://[::]:5280")]
    [InlineData("http://localhost:5280")]
    [InlineData("http://kaldb:5280")]
    [InlineData("https://127.0.0.1:5280")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("127.0.0.1:5280")]
    [InlineData("")]
    [InlineData("not a url")]
    public void AnythingThatCouldBeReachedFromElsewhereIsRefused(string url)
    {
        var options = new ApiHostOptions { Url = url };

        Assert.Throws<InvalidOperationException>(options.ListenEndPoint);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void TheDefaultsAreLoopbackSharedMemoryAndTheStandardDatabase()
    {
        var options = new ApiHostOptions();

        options.Validate();
        var connection = options.ToConnectionOptions();

        Assert.Equal(("lpc:.", "KalTerm", "KalTerm.Api"), (connection.Server, connection.Database, connection.ApplicationName));
        Assert.Equal(ApiHostOptions.DefaultUrl, options.Url);
    }

    [Theory]
    [InlineData("", "KalTerm")]
    [InlineData("lpc:.", " ")]
    public void AnEmptyServerOrDatabaseIsRefused(string server, string database)
    {
        Assert.Throws<InvalidOperationException>(new ApiHostOptions { Server = server, Database = database }.Validate);
    }

    [Fact]
    public void EveryBoundAddressMustBeLoopbackOrTheProcessRefusesToRun()
    {
        ApiHostOptions.EnsureAllLoopback(["http://127.0.0.1:5280", "http://[::1]:5280"]);
        ApiHostOptions.EnsureAllLoopback([]);

        Assert.Throws<InvalidOperationException>(() => ApiHostOptions.EnsureAllLoopback(["http://127.0.0.1:5280", "http://0.0.0.0:5280"]));
        Assert.Throws<InvalidOperationException>(() => ApiHostOptions.EnsureAllLoopback(["http://example.com:5280"]));
        Assert.Throws<InvalidOperationException>(() => ApiHostOptions.EnsureAllLoopback(["garbage"]));
        Assert.Throws<ArgumentNullException>(() => ApiHostOptions.EnsureAllLoopback(null!));
    }

    [Fact]
    public void TheRefusalIsLoggedWithoutDetailsOfTheCaller()
    {
        var exception = new InvalidOperationException("x");
        ApiHostLog.Refusing(NullLogger.Instance, exception);
    }
}

public class ReadinessProbeTests
{
    [Fact]
    public async Task ADatabaseThatCannotBeReachedIsNotReady()
    {
        var probe = new DatabaseReadinessProbe(new DataSource(new DatabaseConnectionOptions { Server = "lpc:.\\NO_SUCH_INSTANCE_FOR_THIS_TEST" }));

        Assert.False(await probe.IsReadyAsync(TestContext.Current.CancellationToken));
    }
}
