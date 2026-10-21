using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Status;

namespace KalTerm.Terminal.Tests;

public class FormattingTests
{
    [Fact]
    public void ATimeIsShownInTheChosenZoneWithItsOffsetThatDay()
    {
        var denver = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        var format = new Formatting(denver);

        Assert.Equal("2026-10-18 17:00:00", format.Moment("2026-10-18T23:00:00.000000+00:00"));
        Assert.Equal("2026-10-18 17:00:00 -06:00", format.MomentWithZone("2026-10-18T23:00:00Z"));
        format.Zone = TimeZoneInfo.Utc;
        Assert.Equal("2026-10-18 23:00:00 UTC", format.MomentWithZone("2026-10-18T23:00:00Z"));
    }

    [Fact]
    public void ChangingTheZoneIsAnnouncedOnceAndOnlyWhenItChanges()
    {
        var format = new Formatting(TimeZoneInfo.Utc);
        var changes = 0;
        format.ZoneChanged += () => changes++;

        format.Zone = TimeZoneInfo.Utc;
        format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal(1, changes);
    }

    [Fact]
    public void WhatIsMissingOrUnreadableIsShownAsItIs()
    {
        var format = new Formatting(TimeZoneInfo.Utc);

        Assert.Equal("-", format.Moment(null));
        Assert.Equal("-", format.MomentWithZone(null));
        Assert.Equal("not a time", format.Moment("not a time"));
        Assert.Equal("not a time", format.MomentWithZone("not a time"));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(45L, "45s ago")]
    [InlineData(119L, "119s ago")]
    [InlineData(720L, "12m ago")]
    [InlineData(18_000L, "5h ago")]
    [InlineData(259_200L, "3d ago")]
    public void AgesAreCoarse(long? seconds, string expected)
    {
        Assert.Equal(expected, Formatting.Age(seconds));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(512L, "512 B")]
    [InlineData(2_048L, "2.0 kB")]
    [InlineData(276_824_064L, "264.0 MB")]
    [InlineData(3_221_225_472L, "3.0 GB")]
    public void SizesUseTheLargestFittingUnit(long? bytes, string expected)
    {
        Assert.Equal(expected, Formatting.Size(bytes));
    }

    [Theory]
    [InlineData(3L, 1000L, "0.3%")]
    [InlineData(500L, 1000L, "50.0%")]
    [InlineData(1L, 0L, "-")]
    public void PercentagesUseIntegersOnly(long part, long whole, string expected)
    {
        Assert.Equal(expected, Formatting.Percent(part, whole));
    }
}

public class StatusPresenterTests
{
    private static readonly Formatting Utc = new(TimeZoneInfo.Utc);

    private static StatusLine Line(StatusPage page, string label) => page.Lines.Single(l => l.Label == label);

    [Fact]
    public void AHealthyServerShowsEveryAreaAndNoProblems()
    {
        var page = StatusPresenter.Present(Samples.Status(), Utc);

        Assert.Equal("Server status 2026-10-18 23:00:00 UTC", page.Heading);
        Assert.Empty(page.Problems);
        Assert.Equal(["Ingest", "Streams", "Storage", "Retention", "Markets", "Books", "Backup", "Analytics", "Gaps"], page.Lines.Select(l => l.Label));
        Assert.All(page.Lines, l => Assert.Equal(Severity.Normal, l.Severity));
        Assert.Equal("running  last heartbeat 3s ago (2026-10-18 22:59:57 UTC)", Line(page, "Ingest").Text);
        Assert.Equal("ticker 1s ago  |  trade 2s ago", Line(page, "Streams").Text);
        Assert.Equal("database used 251.8 MB of 100.0 GB (0.2%), file 264.0 MB  |  disk free 920.1 GB", Line(page, "Storage").Text);
        Assert.Equal("oldest raw data 2026-10-17  |  governor ok used=0% dropped= 40s ago", Line(page, "Retention").Text);
        Assert.Equal("active 120,018  |  unlisted 428  |  closed 288", Line(page, "Markets").Text);
        Assert.Equal("2 watched, none stale, published 3s ago", Line(page, "Books").Text);
        Assert.Equal("not configured", Line(page, "Backup").Text);
        Assert.Equal("not reported", Line(page, "Analytics").Text);
        Assert.Equal("2 in the last 24 h  (ingest_down 2)", Line(page, "Gaps").Text);
        Assert.Equal("Ticker updates in a gap are lost.", page.GapNote);
    }

    [Fact]
    public void ProblemsComeThroughAsTheServerWroteThem()
    {
        var page = StatusPresenter.Present(Samples.Status("ingest is not running", "disk almost full"), Utc);

        Assert.Equal(["ingest is not running", "disk almost full"], page.Problems);
    }

    [Theory]
    [InlineData("stopped", "Error")]
    [InlineData("starting", "Warning")]
    [InlineData("running", "Normal")]
    public void TheIngestStateSetsTheSeverity(string state, string expectedName)
    {
        var status = Samples.Status() with { Ingest = new IngestStateDto(state, null, null) };

        Assert.Equal(Enum.Parse<Severity>(expectedName), Line(StatusPresenter.Present(status, Utc), "Ingest").Severity);
    }

    [Fact]
    public void AStreamWithNothingIn24HoursIsAnError()
    {
        var status = Samples.Status() with { Streams = new StreamsDto(new StreamDto(null, null), new StreamDto("2026-10-18T22:59:58Z", 2)) };

        var line = Line(StatusPresenter.Present(status, Utc), "Streams");

        Assert.Equal(("ticker none in 24 h  |  trade 2s ago", Severity.Error), (line.Text, line.Severity));
    }

    [Fact]
    public void StorageWithNoCapAndAGovernorErrorAreSaid()
    {
        var storage = Samples.Status().Storage with { MaxBytes = null, GovernorLast = "error disk", OldestDataDay = null };
        var page = StatusPresenter.Present(Samples.Status() with { Storage = storage }, Utc);

        Assert.Contains("no cap", Line(page, "Storage").Text, StringComparison.Ordinal);
        Assert.Equal(Severity.Error, Line(page, "Retention").Severity);
        Assert.Contains("oldest raw data -", Line(page, "Retention").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMarketsAndNoGovernorRunAreSaid()
    {
        var storage = Samples.Status().Storage with { GovernorLast = null };
        var page = StatusPresenter.Present(Samples.Status() with { Markets = new Dictionary<string, long>(), Storage = storage }, Utc);

        Assert.Equal("none", Line(page, "Markets").Text);
        Assert.Contains("has not run", Line(page, "Retention").Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0, "none watched", "Normal")]
    [InlineData(2, 1, 0, "2 watched, 1 stale, published 3s ago", "Error")]
    [InlineData(2, 0, 1, "2 watched, none stale, 1 approximate, published 3s ago", "Warning")]
    public void BooksAreJudgedByStaleAndApproximate(int watched, int stale, int approximate, string text, string severityName)
    {
        var status = Samples.Status() with { Books = new BooksStatusDto(watched, stale, approximate, 3) };

        var line = Line(StatusPresenter.Present(status, Utc), "Books");

        Assert.Equal((text, Enum.Parse<Severity>(severityName)), (line.Text, line.Severity));
    }

    [Fact]
    public void BackupsShowTheLastGoodTheProofAndEveryFailure()
    {
        var good = new BackupStatusDto(true, "full-1.bak", "2026-10-18T08:00:00Z", 39_600, "ok full", "2026-10-18T08:00:00Z", "ok restored", "2026-10-18T08:05:00Z");
        var failedAttempt = good with { Last = "error disk full", LastAt = "2026-10-18T20:00:00Z" };
        var failedProof = good with { LastVerify = "error mismatch" };
        var none = new BackupStatusDto(true, null, null, null, null, null, null, null);

        StatusLine Of(BackupStatusDto backup) => Line(StatusPresenter.Present(Samples.Status() with { Backup = backup }, Utc), "Backup");

        Assert.Equal(("last good full-1.bak, 11h ago  |  last proof by restore ok", Severity.Normal), (Of(good).Text, Of(good).Severity));
        Assert.Equal(Severity.Error, Of(failedAttempt).Severity);
        Assert.Contains("last attempt failed: error disk full", Of(failedAttempt).Text, StringComparison.Ordinal);
        Assert.Contains("proof failed: error mismatch", Of(failedProof).Text, StringComparison.Ordinal);
        Assert.Equal(("no finished backup yet", Severity.Error), (Of(none).Text, Of(none).Severity));
        Assert.Equal("not configured", Of(new BackupStatusDto(false, null, null, null, null, null, null, null)).Text);
    }

    [Fact]
    public void AnalyticsShowTheLastRunAndFailuresAreErrors()
    {
        var ok = new AnalyticsStatusDto("ok", "20 written", "2026-10-18T22:55:00Z", "2026-10-18T22:55:00Z", 300, 13, 200_000);
        var failed = new AnalyticsStatusDto("failed", "timeout in sql", "2026-10-18T22:55:00Z", null, null, null, null);

        StatusLine Of(AnalyticsStatusDto analytics) => Line(StatusPresenter.Present(Samples.Status() with { Analytics = analytics }, Utc), "Analytics");

        Assert.Equal(("last run ok  |  screener figures 5m ago, 200,000 markets in 13 s", Severity.Normal), (Of(ok).Text, Of(ok).Severity));
        Assert.Equal(("last run failed: timeout in sql  |  no successful run yet", Severity.Error), (Of(failed).Text, Of(failed).Severity));
        Assert.Equal("no run yet", Of(new AnalyticsStatusDto(null, null, null, null, null, null, null)).Text);
    }

    [Fact]
    public void GapsShowKindsAndTheTradeBackfill()
    {
        var gaps = new GapSummaryDto(24, 3, new Dictionary<string, long> { ["ingest_down"] = 2, ["stream"] = 1 }, "note", 1, 2);

        var line = Line(StatusPresenter.Present(Samples.Status() with { Gaps = gaps }, Utc), "Gaps");

        Assert.Equal(("3 in the last 24 h  (ingest_down 2, stream 1)  |  trade backfill: 1 waiting, 2 failed", Severity.Error), (line.Text, line.Severity));
        Assert.Equal("0 in the last 24 h", Line(StatusPresenter.Present(Samples.Status() with { Gaps = new GapSummaryDto(24, 0, new Dictionary<string, long>(), "n") }, Utc), "Gaps").Text);
    }
}

public sealed class StatusViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly StatusViewModel _model;

    public StatusViewModelTests() => _model = new StatusViewModel(_format, _t.Time);

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeAnythingLoadsItSaysSoAndAskingWithoutAServerDoesNothing()
    {
        Assert.True(_model.Loading);
        Assert.False(_model.HasPage);
        Assert.Equal("not loaded yet", _model.UpdatedText);

        Assert.False(await _model.RefreshAsync(Ct));
    }

    [Fact]
    public async Task ARefreshShowsThePageAndSaysWhenItWasUpdated()
    {
        _model.Attach(_t.Api);

        Assert.True(await _model.RefreshAsync(Ct));

        Assert.True(_model.HasPage);
        Assert.True(_model.AllClear);
        Assert.False(_model.HasProblems);
        Assert.False(_model.Loading);
        Assert.Equal("Server status 2026-10-18 23:00:00 UTC", _model.Page!.Heading);
        Assert.Equal("updated 2026-10-18 23:00:00 (0s ago)", _model.UpdatedText);
        Assert.False(_model.Stale);
    }

    [Fact]
    public async Task ProblemsAreFlaggedAndTheEventsTellTheShell()
    {
        _t.Api.Status = Samples.Status("disk almost full");
        _model.Attach(_t.Api);
        var succeeded = 0;
        _model.Succeeded += () => succeeded++;

        await _model.RefreshAsync(Ct);

        Assert.True(_model.HasProblems);
        Assert.False(_model.AllClear);
        Assert.Equal(1, succeeded);
    }

    [Theory]
    [InlineData("unauthorized", "the server did not accept the token")]
    [InlineData("server_error", "the API refused the request (500 server_error)")]
    public async Task AFailureKeepsTheLastPageGreysItAfterThreeIntervalsAndIsReported(string code, string message)
    {
        _model.Attach(_t.Api);
        await _model.RefreshAsync(Ct);
        _t.Api.StatusFails = new ApiException(code == "unauthorized" ? System.Net.HttpStatusCode.Unauthorized : System.Net.HttpStatusCode.InternalServerError, code);
        Exception? reported = null;
        _model.Failed += ex => reported = ex;

        _t.Time.Advance(StatusViewModel.Interval * 4);
        var worked = await _model.RefreshAsync(Ct);

        Assert.False(worked);
        Assert.True(_model.HasPage);   // the last page stays, greyed
        Assert.True(_model.Stale);
        Assert.Equal(message, _model.Error);
        Assert.True(_model.HasError);
        Assert.IsType<ApiException>(reported);
        Assert.Contains("(40s ago)", _model.UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerAndAnUntrustedCertificateAreReportedWithTheirOwnWords()
    {
        _model.Attach(_t.Api);
        _t.Api.StatusFails = new ApiUnavailableException("could not reach kaldb:5280");
        Exception? reported = null;
        _model.Failed += ex => reported = ex;

        await _model.RefreshAsync(Ct);
        Assert.Equal("could not reach kaldb:5280", _model.Error);
        Assert.IsType<ApiUnavailableException>(reported);
        Assert.False(_model.Loading);   // there is a reason to show instead of "Loading"

        _t.Api.StatusFails = new TlsTrustException(TrustFailure.Changed, "kaldb:5280", Samples.Fingerprint, Samples.Fingerprint, new InvalidOperationException());
        await _model.RefreshAsync(Ct);
        Assert.StartsWith("WARNING: the certificate of kaldb:5280 CHANGED", _model.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheZoneRedrawsTheShownTimes()
    {
        _model.Attach(_t.Api);
        await _model.RefreshAsync(Ct);

        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal("Server status 2026-10-18 17:00:00 -06:00", _model.Page!.Heading);
        Assert.StartsWith("updated 2026-10-18 17:00:00", _model.UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollingAsksAgainEveryIntervalUntilDetached()
    {
        _model.Attach(_t.Api);
        await _model.RefreshAsync(Ct);
        _model.StartPolling();
        await WaitAsync(() => _t.Time.PendingTimers == 1);   // the loop is waiting for its interval; firing before it is would find nothing

        _t.Time.FireNext();
        await WaitAsync(() => _t.Api.StatusCalls == 2);
        await WaitAsync(() => _t.Time.PendingTimers == 1);
        _t.Time.FireNext();
        await WaitAsync(() => _t.Api.StatusCalls == 3);
        _model.Detach();
        await Task.Delay(50, Ct);

        Assert.Equal(3, _t.Api.StatusCalls);
        Assert.True(_t.Api.Disposed);
        Assert.False(await _model.RefreshAsync(Ct));
    }

    [Fact]
    public async Task StartingPollingAgainReplacesTheOldLoopAndAttachingForgetsThePage()
    {
        _model.Attach(_t.Api);
        await _model.RefreshAsync(Ct);
        _model.StartPolling();
        _model.StartPolling();
        _model.Attach(_t.Api);

        Assert.False(_model.HasPage);
        Assert.True(_model.Loading);
        Assert.Equal("not loaded yet", _model.UpdatedText);
    }

    [Fact]
    public async Task ARefreshThatIsCancelledIsNotAFailure()
    {
        _model.Attach(_t.Api);
        _t.Api.StatusFails = new OperationCanceledException();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.False(await _model.RefreshAsync(cancelled.Token));
        Assert.False(_model.HasError);
    }

    [Fact]
    public async Task TheRefreshCommandRunsARefreshAndReportsAnUnexpectedFailure()
    {
        _model.Attach(_t.Api);

        await _model.RefreshCommand.RunAsync();
        Assert.True(_model.HasPage);

        _t.Api.StatusFails = new InvalidOperationException("boom");
        await _model.RefreshCommand.RunAsync();
        Assert.Equal("boom", _model.Error);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }
    }
}
