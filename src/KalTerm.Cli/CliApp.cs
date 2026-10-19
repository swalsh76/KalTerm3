using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Client;
using KalTerm.Contracts;
using Spectre.Console;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>
/// The command line: <c>status</c>, <c>markets</c>, <c>market</c>, <c>ticks</c>, <c>trades</c> and <c>gaps</c>. Everything is read-only. Exit codes: 0 fine, 1 the API
/// could not be reached or refused the request (or the command line was wrong), 2 <c>status</c> found problems that need attention.
/// </summary>
internal static partial class CliApp
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NeedsAttention = 2;

    private static readonly TimeSpan DefaultGapLookback = TimeSpan.FromHours(24);

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = env.Out.Profile.Out.Writer, Error = env.Error.Profile.Out.Writer, EnableDefaultExceptionHandler = true };
        return await BuildRoot(env).Parse(args).InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand BuildRoot(CliEnvironment env)
    {
        var api = new Option<string?>("--api") { Description = $"The API address (default: {CliEnvironment.ApiVariable} or {KalTermApiClient.DefaultBaseAddress})", Recursive = true };
        var json = new Option<bool>("--json") { Description = "Print the API's JSON instead of a table", Recursive = true };
        var width = new Option<int?>("--width") { Description = "Output width in characters", Recursive = true };
        var noColor = new Option<bool>("--no-color") { Description = "No colors or styles", Recursive = true };

        var root = new RootCommand("kterm: a read-only view of a KalTerm server");
        foreach (var option in new Option[] { api, json, width, noColor })
        {
            root.Options.Add(option);
        }

        Task<int> Run(ParseResult parsed, CancellationToken ct, Func<IKalTermApi, bool, Task<int>> body) =>
            Execute(env, parsed.GetValue(api), parsed.GetValue(width), parsed.GetValue(noColor), parsed.GetValue(json), body, ct);

        // ---- status ----
        var status = new Command("status", "Health of the server: problems first, then ingest, streams, storage, markets and gaps");
        status.SetAction((parsed, ct) => Run(parsed, ct, async (client, asJson) =>
        {
            var report = await client.GetStatusAsync(ct).ConfigureAwait(false);
            Print(env, asJson, report, Views.Status(report));
            return report.Problems.Count > 0 ? NeedsAttention : Ok;
        }));
        root.Subcommands.Add(status);

        // ---- markets ----
        var statusFilter = new Option<string?>("--status") { Description = "Only markets with this status, for example active or closed" };
        var eventFilter = new Option<string?>("--event") { Description = "Only markets of this event" };
        var search = new Option<string?>("--search", "-q") { Description = "Only tickers containing this text (at least 2 characters)" };
        var marketsLimit = new Option<int>("--limit") { Description = "How many to show", DefaultValueFactory = _ => 25 };
        var marketsCursor = new Option<string?>("--cursor") { Description = "Continue after the previous page (the cursor it printed)" };
        var markets = new Command("markets", "List markets in ticker order");
        foreach (var option in new Option[] { statusFilter, eventFilter, search, marketsLimit, marketsCursor })
        {
            markets.Options.Add(option);
        }

        markets.SetAction((parsed, ct) => Run(parsed, ct, async (client, asJson) =>
        {
            var page = await client.GetMarketsAsync(new MarketsQuery(parsed.GetValue(statusFilter), parsed.GetValue(eventFilter), parsed.GetValue(search), parsed.GetValue(marketsLimit), parsed.GetValue(marketsCursor)), ct).ConfigureAwait(false);
            Print(env, asJson, page, Views.Markets(page, env.Out.Profile.Width));
            return Ok;
        }));
        root.Subcommands.Add(markets);

        // ---- market ----
        var marketTicker = new Argument<string>("ticker") { Description = "The market ticker" };
        var market = new Command("market", "One market: its details, latest tick and recent trades (with --json: the market only)");
        market.Arguments.Add(marketTicker);
        market.SetAction((parsed, ct) => Run(parsed, ct, async (client, asJson) =>
        {
            var ticker = parsed.GetRequiredValue(marketTicker);
            var found = await client.GetMarketAsync(ticker, ct).ConfigureAwait(false);
            if (asJson)
            {
                Print(env, true, found, new Text(string.Empty));
                return Ok;
            }

            var ticks = client.GetTicksAsync(ticker, new WindowQuery(Limit: 1), ct);
            var trades = client.GetTradesAsync(ticker, new WindowQuery(Limit: 5), ct);
            await Task.WhenAll(ticks, trades).ConfigureAwait(false);
            var latest = (await ticks.ConfigureAwait(false)).Ticks;
            env.Out.Write(Views.Market(found, latest.Count > 0 ? latest[^1] : null, (await trades.ConfigureAwait(false)).Trades, env.Out.Profile.Width));
            return Ok;
        }));
        root.Subcommands.Add(market);

        // ---- ticks and trades ----
        root.Subcommands.Add(WindowCommand("ticks", "Ticker updates of a market, oldest first (the latest ones unless --since or --start is given)", env, Run,
            (client, ticker, query, asJson, ct) => ReadTicks(env, client, ticker, query, asJson, ct)));
        root.Subcommands.Add(WindowCommand("trades", "Trades of a market, oldest first (the latest ones unless --since or --start is given)", env, Run,
            (client, ticker, query, asJson, ct) => ReadTrades(env, client, ticker, query, asJson, ct)));

        // ---- gaps ----
        var gapSince = new Option<string?>("--since") { Description = "How far back to look, for example 30m, 12h or 7d (default 24h)" };
        var gapLimit = new Option<int>("--limit") { Description = "How many to show", DefaultValueFactory = _ => 50 };
        var gaps = new Command("gaps", "Periods the stream data may have missed, newest first (including downtime)");
        gaps.Options.Add(gapSince);
        gaps.Options.Add(gapLimit);
        gaps.SetAction((parsed, ct) => Run(parsed, ct, async (client, asJson) =>
        {
            var lookback = DefaultGapLookback;
            if (parsed.GetValue(gapSince) is { } text && !TryParseSince(text, out lookback))
            {
                return Fail(env, "--since must be a number and a unit, for example 30m, 12h or 7d");
            }

            var now = env.Time.GetUtcNow().UtcDateTime;
            var page = await client.GetGapsAsync(new WindowQuery(Start: now - lookback, Limit: parsed.GetValue(gapLimit)), ct).ConfigureAwait(false);
            Print(env, asJson, page, Views.Gaps(page, now, env.Out.Profile.Width));
            return Ok;
        }));
        root.Subcommands.Add(gaps);

        return root;
    }

    private static Command WindowCommand(
        string name,
        string description,
        CliEnvironment env,
        Func<ParseResult, CancellationToken, Func<IKalTermApi, bool, Task<int>>, Task<int>> run,
        Func<IKalTermApi, string, WindowQuery, bool, CancellationToken, Task<int>> read)
    {
        var ticker = new Argument<string>("ticker") { Description = "The market ticker" };
        var since = new Option<string?>("--since") { Description = "From this long ago, for example 15m, 2h or 1d" };
        var start = new Option<string?>("--start") { Description = "From this moment, with its offset, for example 2026-10-18T22:00:00Z" };
        var end = new Option<string?>("--end") { Description = "Until this moment (exclusive), with its offset" };
        var limit = new Option<int>("--limit") { Description = "How many to show", DefaultValueFactory = _ => 20 };
        var cursor = new Option<string?>("--cursor") { Description = "Continue after the previous page (the cursor it printed)" };
        var command = new Command(name, description);
        command.Arguments.Add(ticker);
        foreach (var option in new Option[] { since, start, end, limit, cursor })
        {
            command.Options.Add(option);
        }

        command.SetAction((parsed, ct) => run(parsed, ct, async (client, asJson) =>
        {
            var query = Window(env, parsed.GetValue(since), parsed.GetValue(start), parsed.GetValue(end), parsed.GetValue(limit), parsed.GetValue(cursor), out var problem);
            return query is null ? Fail(env, problem!) : await read(client, parsed.GetRequiredValue(ticker), query, asJson, ct).ConfigureAwait(false);
        }));
        return command;
    }

    private static async Task<int> ReadTicks(CliEnvironment env, IKalTermApi client, string ticker, WindowQuery query, bool asJson, CancellationToken ct)
    {
        var page = await client.GetTicksAsync(ticker, query, ct).ConfigureAwait(false);
        Print(env, asJson, page, Views.Ticks(page, env.Out.Profile.Width));
        return Ok;
    }

    private static async Task<int> ReadTrades(CliEnvironment env, IKalTermApi client, string ticker, WindowQuery query, bool asJson, CancellationToken ct)
    {
        var page = await client.GetTradesAsync(ticker, query, ct).ConfigureAwait(false);
        Print(env, asJson, page, Views.Trades(page, env.Out.Profile.Width));
        return Ok;
    }

    /// <summary>The window of a ticks or trades read; <see langword="null"/> (with the reason) when the options contradict each other or a time is unusable.</summary>
    private static WindowQuery? Window(CliEnvironment env, string? since, string? start, string? end, int limit, string? cursor, out string? problem)
    {
        problem = null;
        DateTime? from = null;
        DateTime? until = null;
        if (since is not null && start is not null)
        {
            problem = "use either --since or --start, not both";
            return null;
        }

        if (since is not null)
        {
            if (!TryParseSince(since, out var span))
            {
                problem = "--since must be a number and a unit, for example 15m, 2h or 1d";
                return null;
            }

            from = env.Time.GetUtcNow().UtcDateTime - span;
        }

        if (start is not null)
        {
            if (!TryParseTime(start, out var parsedStart))
            {
                problem = "--start must be a time with its offset, for example 2026-10-18T22:00:00Z";
                return null;
            }

            from = parsedStart;
        }

        if (end is not null)
        {
            if (!TryParseTime(end, out var parsedEnd))
            {
                problem = "--end must be a time with its offset, for example 2026-10-18T23:00:00Z";
                return null;
            }

            until = parsedEnd;
        }

        return new WindowQuery(from, until, limit, cursor);
    }

    /// <summary>Parses a time that carries its offset (<c>Z</c> or <c>+hh:mm</c>); one without is ambiguous and refused. The result is UTC.</summary>
    public static bool TryParseTime(string text, out DateTime utc)
    {
        utc = default;
        if (!Offset().IsMatch(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    private static async Task<int> Execute(CliEnvironment env, string? apiOption, int? width, bool noColor, bool asJson, Func<IKalTermApi, bool, Task<int>> body, CancellationToken cancellationToken)
    {
        if (width is { } columns)
        {
            env.Out.Profile.Width = Math.Clamp(columns, 40, 400);
        }

        if (noColor)
        {
            env.Out.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
            env.Out.Profile.Capabilities.Ansi = false;
        }

        var address = apiOption ?? env.GetVariable(CliEnvironment.ApiVariable) ?? KalTermApiClient.DefaultBaseAddress;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Fail(env, $"the API address '{address}' is not an http or https address");
        }

        var client = env.CreateApi(uri);
        try
        {
            return await body(client, asJson).ConfigureAwait(false);
        }
        catch (ApiUnavailableException ex)
        {
            return Fail(env, ex.Message);
        }
        catch (ApiException ex)
        {
            return Fail(env, ex.Code == "not_found" ? "not found" : ex.Message);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>Prints the API's own JSON for <c>--json</c>, else the rendered view. The JSON goes straight to the writer so it is never wrapped or restyled.</summary>
    private static void Print<T>(CliEnvironment env, bool asJson, T model, Spectre.Console.Rendering.IRenderable view)
        where T : class
    {
        if (asJson)
        {
            env.Out.Profile.Out.Writer.WriteLine(ApiJson.Serialize(model));
        }
        else
        {
            env.Out.Write(view);
        }
    }

    private static int Fail(CliEnvironment env, string message)
    {
        env.Error.MarkupLine($"[red]error:[/] {Escape(message)}");
        return Failed;
    }

    [GeneratedRegex("(Z|z|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
