using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.Cli.Tests;

/// <summary><c>kterm watch</c> while it runs: lines typed to add or remove markets, what is said back, and the confirmations the stream sends.</summary>
public sealed class MarketWatchChangeCommandTests : IDisposable
{
    private readonly Harness _cli = new() { AutoFireTimers = true, Interactive = true };
    private readonly CancellationTokenSource _stop = new();

    public MarketWatchChangeCommandTests()
    {
        _cli.Api.StopWhenScriptEnds = _stop;
        _cli.Api.HoldStreamOpen = true;
    }

    public void Dispose()
    {
        _stop.Dispose();
        _cli.Dispose();
    }

    private static ReadyEvent Ready() => new(new StreamReadyDto("abc", "2026-10-18T22:59:58.000Z", ["A-1"], [], [], [], 10));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs the watch until the condition holds, then stops it and returns its exit code.</summary>
    private async Task<int> RunUntilAsync(Func<bool> done, params string[] args)
    {
        var run = _cli.RunAsync(_stop.Token, args);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done())
        {
            await Task.Delay(10, guard.Token);
        }

        await _stop.CancelAsync();
        return await run;
    }

    [Fact]
    public async Task TypedLinesAddAndRemoveMarketsOnTheOpenStream()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready() });
        _cli.Type("+B-2", "+C-3 book", "-A-1", "-C-3 book");

        var exit = await RunUntilAsync(() => _cli.Api.MarketChanges.Count == 4, "watch", "A-1");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(["add abc ticks B-2", "add abc books C-3", "remove abc ticks A-1", "remove abc books C-3"], _cli.Api.MarketChanges);
        Assert.Contains("type +TICKER", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineThatIsNotACommandIsSaidAndTheWatchGoesOn()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready() });
        _cli.Type("hello", "+", "+A-1 book extra", "+A-1 foo", string.Empty, "+B-2");

        await RunUntilAsync(() => _cli.Api.MarketChanges.Count == 1, "watch", "A-1");

        Assert.Equal(["add abc ticks B-2"], _cli.Api.MarketChanges);
        Assert.Equal(1 + 4, _cli.Error.Split("type +TICKER, +TICKER book, -TICKER or -TICKER book", StringSplitOptions.None).Length - 1);   // the opening notice, then one for each of the four lines that are not commands (the empty one is skipped)
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "too_many_markets", "as many markets as it can")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter", "invalid_parameter")]
    public async Task AChangeTheServerRefusesIsSaidAndTheWatchGoesOn(HttpStatusCode status, string code, string said)
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready() });
        _cli.Api.MarketChangeFailure = new ApiException(status, code);
        _cli.Type("+B-2", "+C-3");

        await RunUntilAsync(() => _cli.Api.MarketChanges.Count == 2, "watch", "A-1");

        Assert.Contains(said, _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmationsAreSaidOnTheErrorOutput()
    {
        var added = new StreamSubscriptionDto("ticks", "B-2", "2026-10-18T23:00:01.000Z", false, false);
        var unknown = new StreamSubscriptionDto("ticks", "Z-9", "2026-10-18T23:00:01.000Z", true, false);
        var noBook = new StreamSubscriptionDto("books", "C-3", null, false, true);
        var removed = new StreamSubscriptionDto("ticks", "A-1", null, false, false);
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready(), new SubscribedEvent(added), new SubscribedEvent(unknown), new SubscribedEvent(noBook), new UnsubscribedEvent(removed) });

        await RunUntilAsync(() => _cli.Error.Contains("stopped watching", StringComparison.Ordinal), "watch", "A-1");

        Assert.Contains("now watching B-2 (ticks), live ticks from 2026-10-18T23:00:01.000Z", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("Z-9 is not a market here", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("now watching C-3 (books)", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("no book published for C-3", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("stopped watching A-1 (ticks)", _cli.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("now watching", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithJsonTheConfirmationsAreEventsToo()
    {
        var added = new StreamSubscriptionDto("books", "B-2", null, false, false);
        var removed = new StreamSubscriptionDto("ticks", "A-1", null, false, false);
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready(), new SubscribedEvent(added), new UnsubscribedEvent(removed) });

        await RunUntilAsync(() => _cli.Output.Contains("\"unsubscribed\"", StringComparison.Ordinal), "watch", "A-1", "--json");

        var lines = _cli.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["ready", "subscribed", "unsubscribed"], lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()));
        Assert.Contains("\"ticker\":\"B-2\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenInputIsNotATerminalNothingIsReadAndTheNoticeDoesNotMentionTyping()
    {
        var piped = new Harness { AutoFireTimers = true, Interactive = false };
        using var stop = new CancellationTokenSource();
        piped.Api.StopWhenScriptEnds = stop;
        piped.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Ready() });
        piped.Type("+B-2");

        await piped.RunAsync(stop.Token, "watch", "A-1");

        Assert.Empty(piped.Api.MarketChanges);
        Assert.DoesNotContain("type +TICKER", piped.Error, StringComparison.Ordinal);
        piped.Dispose();
    }
}
