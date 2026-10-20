using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.Cli.Tests;

/// <summary><c>kterm watch</c>: ticks and books as they arrive, notices on stderr, JSON lines, and a stop on Ctrl+C.</summary>
public sealed class MarketWatchCommandTests : IDisposable
{
    private readonly Harness _cli = new() { AutoFireTimers = true };
    private readonly CancellationTokenSource _stop = new();

    public MarketWatchCommandTests() => _cli.Api.StopWhenScriptEnds = _stop;

    public void Dispose()
    {
        _stop.Dispose();
        _cli.Dispose();
    }

    private static StreamReadyDto ReadyDto(string[]? unknown = null, string[]? noBook = null) => new("abc", "2026-10-18T22:59:58.000Z", ["A-1"], ["A-1"], unknown ?? [], noBook ?? [], 10);

    private static TickEvent Tick(string price = "0.560000") =>
        new(new MarketTickDto("A-1", new TickDto("2026-10-18T22:59:59.250000+00:00", price, "0.550000", "0.570000", "1.00", "2.00", "1234.00", null, null)), "2026-10-18T22:59:59.250Z");

    private static BookEvent Book(bool stale = false, bool approximate = false, bool empty = false) =>
        new(new OrderBookDto(
            "A-1",
            "2026-10-18T22:59:50.250000+00:00",
            "2026-10-18T22:59:58.000000+00:00",
            2,
            stale,
            approximate,
            empty ? [] : [new BookLevelDto("0.410000", "1.50")],
            empty ? [] : [new BookLevelDto("0.540000", "7.00")]));

    [Fact]
    public async Task TicksAndBooksArePrintedAsTheyComeAndTheOpeningNoticeGoesToTheErrorOutput()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto()), Book(), Tick() });

        var exit = await _cli.RunAsync(_stop.Token, "watch", "A-1", "--book");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("2026-10-18 22:59:59.250 UTC  A-1  price 0.56  bid 0.55  ask 0.57  volume 1,234", _cli.Output, StringComparison.Ordinal);
        var book = _cli.Output.ReplaceLineEndings("|").Split('|').Single(l => l.Contains("A-1  book", StringComparison.Ordinal));
        Assert.Contains("bid 0.41 x1.5", book, StringComparison.Ordinal);
        Assert.Contains("ask 0.46 x7", book, StringComparison.Ordinal);   // the yes ask is the complement of the best no bid
        Assert.EndsWith("live", book.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("stream open: 1 market(s) for ticks, 1 for books", _cli.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("stream open", _cli.Output, StringComparison.Ordinal);
        Assert.Equal(["A-1"], _cli.Api.MarketOpens[0].Ticks);
        Assert.Equal(["A-1"], _cli.Api.MarketOpens[0].Books);
    }

    [Fact]
    public async Task WithoutBookOnlyTicksAreAskedFor()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto()), Tick() });

        await _cli.RunAsync(_stop.Token, "watch", "A-1", "B-2", "--depth", "3");

        var open = _cli.Api.MarketOpens[0];
        Assert.Equal(["A-1", "B-2"], open.Ticks);
        Assert.Empty(open.Books);
        Assert.Equal(3, open.Depth);
    }

    [Fact]
    public async Task MarketsThatAreNotMarketsAndMarketsWithNoBookAreSaid()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto(unknown: ["Z-9"], noBook: ["A-1"])) });

        await _cli.RunAsync(_stop.Token, "watch", "A-1", "Z-9", "--book");

        Assert.Contains("not markets here: Z-9", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("no book published for: A-1", _cli.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "STALE")]
    [InlineData(false, true, "APPROXIMATE")]
    public async Task ABookThatCannotBeTrustedSaysSo(bool stale, bool approximate, string word)
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Book(stale, approximate) });

        await _cli.RunAsync(_stop.Token, "watch", "A-1", "--book");

        Assert.Contains(word, _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyBookShowsDashesNotZeros()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { Book(empty: true) });

        await _cli.RunAsync(_stop.Token, "watch", "A-1", "--book");

        Assert.Contains("book  bid - x-  ask - x-", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResetIsSaidAndTheWatchCarriesOn()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ResetEvent("too_far_behind") });
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto()), Tick() });

        await _cli.RunAsync(_stop.Token, "watch", "A-1");

        Assert.Contains("the server asked for a reload (too_far_behind)", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("A-1  price 0.56", _cli.Output, StringComparison.Ordinal);
        Assert.Null(_cli.Api.MarketOpens[1].ResumeFrom);
    }

    [Fact]
    public async Task WithJsonEachEventIsOneLineNamingItsKind()
    {
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto()), Book(), Tick(), new ResetEvent("slow_consumer") });

        await _cli.RunAsync(_stop.Token, "watch", "A-1", "--book", "--json");

        var lines = _cli.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["ready", "book", "tick", "reset"], lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()));
        Assert.Contains("\"price\":\"0.560000\"", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("stream open", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamThatDropsIsReopenedAndTheNoticeIsPrinted()
    {
        _cli.Api.MarketScript.Enqueue(new ApiUnavailableException("could not reach the API"));
        _cli.Api.MarketScript.Enqueue(new MarketStreamEvent[] { new ReadyEvent(ReadyDto()), Tick() });

        await _cli.RunAsync(_stop.Token, "watch", "A-1");

        Assert.Contains("could not reach the API", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("A-1  price 0.56", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTokenEndsTheWatchWithOneClearLine()
    {
        _cli.Api.MarketScript.Enqueue(new ApiException(HttpStatusCode.Unauthorized, "unauthorized"));

        var exit = await _cli.RunAsync(_stop.Token, "watch", "A-1");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("did not accept the token", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATickerItIsAUsageError()
    {
        var exit = await _cli.RunAsync(_stop.Token, "watch");

        Assert.NotEqual(CliApp.Ok, exit);
    }
}
