using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.Cli.Tests;

/// <summary><c>kterm alert watch</c>: fires as they arrive, a reconnect from the last id, and a stop on Ctrl+C.</summary>
public sealed class WatchCommandTests : IDisposable
{
    private readonly Harness _cli = new() { AutoFireTimers = true };
    private readonly CancellationTokenSource _stop = new();

    public WatchCommandTests() => _cli.Api.StopWhenScriptEnds = _stop;

    public void Dispose()
    {
        _stop.Dispose();
        _cli.Dispose();
    }

    private static AlertFireDto Fire(long id, string value = "0.610000") =>
        new(id, 7, "A-1", "price_above", "0.600000", value, "2026-10-18T22:59:00.250000+00:00");

    private static IReadOnlyList<AlertStreamEvent> Stream(long ready, params AlertFireDto[] fires) =>
        [new AlertStreamEvent(ready, null), .. fires.Select(f => new AlertStreamEvent(f.Id, f))];

    [Fact]
    public async Task FiresArePrintedAsTheyComeAndNothingElseGoesToTheOutput()
    {
        _cli.Api.StreamScript.Enqueue(Stream(40, Fire(41), Fire(42, "0.620000")));

        var exit = await _cli.RunAsync(_stop.Token, "alert", "watch");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("2026-10-18 22:59:00.250 UTC  A-1  price_above 0.600000  0.610000  (alert 7, fire 41)", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("0.620000", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("watching your alerts", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("watching your alerts", _cli.Error, StringComparison.Ordinal);
        Assert.Equal<long?>([null], _cli.Api.StreamOpens.Take(1));
    }

    [Fact]
    public async Task ADroppedStreamIsReopenedFromTheLastIdSeen()
    {
        _cli.Api.StreamScript.Enqueue(Stream(40, Fire(41)));
        _cli.Api.StreamScript.Enqueue(new ApiUnavailableException("could not reach the API"));
        _cli.Api.StreamScript.Enqueue(Stream(41, Fire(43)));

        await _cli.RunAsync(_stop.Token, "alert", "watch");

        Assert.Equal<long?>([null, 41, 41, 43], _cli.Api.StreamOpens);
        Assert.Contains("fire 43", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("reconnecting", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("could not reach the API", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterReplaysFromThatIdAndJsonPrintsOneObjectPerLine()
    {
        _cli.Api.StreamScript.Enqueue(Stream(10, Fire(11), Fire(12)));

        await _cli.RunAsync(_stop.Token, "alert", "watch", "--after", "10", "--json");

        Assert.Equal<long?>(10, _cli.Api.StreamOpens[0]);
        var lines = _cli.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal([ApiJson.Serialize(Fire(11)), ApiJson.Serialize(Fire(12))], lines);
    }

    [Fact]
    public async Task ATokenTheServerDoesNotAcceptEndsTheWatchWithTheUsualMessage()
    {
        _cli.Api.StreamScript.Enqueue(new ApiException(HttpStatusCode.Unauthorized, "unauthorized"));

        var exit = await _cli.RunAsync(_stop.Token, "alert", "watch");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("did not accept the token", _cli.Error, StringComparison.Ordinal);
        Assert.Single(_cli.Api.StreamOpens);
    }
}
