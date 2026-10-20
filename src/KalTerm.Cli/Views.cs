using System.Globalization;
using KalTerm.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>What each command prints: pure functions from contract models to Spectre renderables. Every piece of API text is escaped before it reaches markup.</summary>
internal static class Views
{
    public static IRenderable Status(StatusDto status)
    {
        var rows = new List<IRenderable> { new Markup($"[bold]Server status[/] {MomentWithZone(status.Time)}"), new Text(string.Empty) };
        if (status.Problems.Count > 0)
        {
            rows.Add(new Markup($"[bold red]NEEDS ATTENTION ({status.Problems.Count})[/]"));
            rows.AddRange(status.Problems.Select(p => new Markup($"  [red]![/] {Escape(p)}")));
        }
        else
        {
            rows.Add(new Markup("[green]All checks passed.[/]"));
        }

        rows.Add(new Text(string.Empty));
        var state = status.Ingest.State switch { "running" => "[green]running[/]", "stopped" => "[red]stopped[/]", var other => $"[yellow]{Escape(other)}[/]" };
        rows.Add(new Markup($"[bold]Ingest[/]    {state}  last heartbeat {Age(status.Ingest.HeartbeatAgeSeconds)} ({MomentWithZone(status.Ingest.LastHeartbeat)})"));
        rows.Add(new Markup($"[bold]Streams[/]   ticker {Stream(status.Streams.Ticker)}  |  trade {Stream(status.Streams.Trade)}"));
        var storage = status.Storage;
        var cap = storage.MaxBytes is { } max ? $"{Size(max)} ({Percent(storage.UsedBytes, max)})" : "no cap";
        rows.Add(new Markup($"[bold]Storage[/]   database used {Size(storage.UsedBytes)} of {cap}, file {Size(storage.DataFileBytes)}  |  disk free {Size(storage.DiskFreeBytes)}"));
        var governor = storage.GovernorLast is null ? "has not run" : $"{(storage.GovernorLast.StartsWith("error", StringComparison.Ordinal) ? "[red]" + Escape(storage.GovernorLast) + "[/]" : Escape(storage.GovernorLast))} {Age(storage.GovernorAgeSeconds)}";
        rows.Add(new Markup($"[bold]Retention[/] oldest raw data {Escape(storage.OldestDataDay ?? None)}  |  governor {governor}"));
        rows.Add(new Markup($"[bold]Markets[/]   {Escape(MarketCounts(status.Markets))}"));
        rows.Add(new Markup($"[bold]Books[/]     {Books(status.Books)}"));
        rows.Add(new Markup($"[bold]Backup[/]    {BackupLine(status.Backup)}"));
        rows.Add(new Markup($"[bold]Gaps[/]      {status.Gaps.Count} in the last {status.Gaps.WindowHours} h{GapKinds(status.Gaps.ByKind)}{BackfillCounts(status.Gaps)}"));
        rows.Add(new Markup($"[grey]{Escape(status.Gaps.Note)}[/]"));
        return new Rows(rows);
    }

    private static string Books(BooksStatusDto books) =>
        books.Watched == 0
            ? "none watched"
            : $"{books.Watched} watched, {(books.Stale == 0 ? "none stale" : $"[red]{books.Stale} stale[/]")}{(books.Approximate == 0 ? string.Empty : $", {books.Approximate} approximate")}, published {Age(books.PublishedAgeSeconds)}";

    /// <summary>A line per watched market: its best prices (the yes ask is derived from the best no bid) and whether the book can be trusted.</summary>
    public static IRenderable Books(OrderBookPage page, int width)
    {
        if (page.Books.Count == 0)
        {
            return new Markup("[grey]No markets are watched. Set Ingest:WatchList on the server.[/]");
        }

        var table = new TextTable().Column("Ticker").Column("Yes bid", right: true).Column("Yes ask", right: true).Column("Spread", right: true).Column("Book").Column("Published");
        foreach (var book in page.Books)
        {
            var bid = book.YesBids.Count > 0 ? Micros(book.YesBids[0].Price) : null;
            var ask = book.NoBids.Count > 0 && Micros(book.NoBids[0].Price) is { } noBid ? Dollar - noBid : (long?)null;
            table.Row(
                book.Ticker,
                bid is null ? None : Dollars(Format(bid.Value)),
                ask is null ? None : Dollars(Format(ask.Value)),
                bid is { } b && ask is { } a ? Dollars(Format(a - b)) : None,
                Condition(book),
                Age(book.AgeSeconds));
        }

        return new Rows(table.Render(width), new Markup($"[grey]{page.Books.Count} watched. Use: kterm book TICKER for the ladder.[/]"));
    }

    /// <summary>
    /// One book as a ladder: the asks above (highest first), the spread, then the bids. Kalshi sends bids only; a yes ask is derived from a no bid
    /// (1 minus its price), so what is shown as an ask is a no bid turned around.
    /// </summary>
    public static IRenderable Book(OrderBookDto book, int width)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold]{Escape(book.Ticker)}[/]  {ConditionMarkup(book)}"),
            new Markup($"[grey]published {Age(book.AgeSeconds)}; last change {MomentWithZone(book.UpdatedAt, true)}[/]"),
            new Text(string.Empty),
        };
        if (book.Stale)
        {
            rows.Add(new Markup("[red]Messages were missed and the book is being rebuilt: do not trust these levels.[/]"));
        }
        else if (book.Approximate)
        {
            rows.Add(new Markup("[yellow]Rebuilt from a snapshot that cannot be aligned with the live stream: levels may be slightly off.[/]"));
        }

        var table = new TextTable().Column("Side").Column("Price", right: true).Column("Size", right: true);
        var asks = book.NoBids.Select(l => (Price: Micros(l.Price), l.Quantity)).Where(l => l.Price is not null).Select(l => (Price: Dollar - l.Price!.Value, l.Quantity)).Reverse();
        foreach (var (price, quantity) in asks)
        {
            table.Row(new Cell("ask", "red"), Dollars(Format(price)), Count(quantity));
        }

        var bid = book.YesBids.Count > 0 ? Micros(book.YesBids[0].Price) : null;
        var bestAsk = book.NoBids.Count > 0 && Micros(book.NoBids[0].Price) is { } noBid ? Dollar - noBid : (long?)null;
        table.Row(new Cell(bid is { } b && bestAsk is { } a ? "spread " + Dollars(Format(a - b)) : "spread " + None, "grey"), string.Empty, string.Empty);
        foreach (var level in book.YesBids)
        {
            table.Row(new Cell("bid", "green"), Dollars(level.Price), Count(level.Quantity));
        }

        rows.Add(table.Render(width));
        rows.Add(new Markup("[grey]Asks are derived from the no bids. --depth sets how many levels each side shows.[/]"));
        return new Rows(rows);
    }

    private const long Dollar = 1_000_000;

    /// <summary>The API's price text (always six decimals) as whole micro-dollars, so a complement is exact; <see langword="null"/> if it is not that.</summary>
    private static long? Micros(string price) =>
        price.Length >= 8 && price[^7] == '.' && long.TryParse(string.Concat(price.AsSpan(0, price.Length - 7), price.AsSpan(price.Length - 6)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var micros) ? micros : null;

    /// <summary>Micro-dollars back to the API's text.</summary>
    private static string Format(long micros) => string.Create(CultureInfo.InvariantCulture, $"{(micros < 0 ? "-" : string.Empty)}{Math.Abs(micros) / Dollar}.{Math.Abs(micros) % Dollar:D6}");

    private static Cell Condition(OrderBookDto book) =>
        book.Stale ? new Cell("STALE", "red") : book.Approximate ? new Cell("APPROX", "yellow") : new Cell("ok", "green");

    private static string ConditionMarkup(OrderBookDto book) =>
        book.Stale ? "[bold red]STALE[/]" : book.Approximate ? "[bold yellow]APPROXIMATE[/]" : "[green]live[/]";

    public static IRenderable Markets(MarketPage page, int width)
    {
        var table = new TextTable().Column("Ticker").Column("Status").Column($"Closes ({ZoneName})").Column("Event");
        foreach (var market in page.Markets)
        {
            table.Row(market.Ticker, new Cell(market.Status, StatusStyle(market.Status)), Moment(market.CloseTime), market.EventTicker);
        }

        return new Rows(table.Render(width), Footer(page.Markets.Count, page.NextCursor));
    }

    public static IRenderable Market(MarketDto market, TickDto? tick, IReadOnlyList<TradeDto> trades, int width)
    {
        var rows = new List<IRenderable> { new Markup($"[bold]{Escape(market.Ticker)}[/]  {StatusMarkup(market.Status)}"), new Text(string.Empty) };
        void Field(string label, string? value) => rows.Add(new Markup($"  [grey]{Escape(label.PadRight(11))}[/] {Escape(value ?? None)}"));
        Field("Event", market.EventTicker);
        Field("Type", market.Type);
        Field("Yes / No", $"{market.YesLabel} / {market.NoLabel}");
        Field("Opens", market.OpenTime is null ? None : $"{MomentWithZone(market.OpenTime)}");
        Field("Closes", market.CloseTime is null ? None : $"{MomentWithZone(market.CloseTime)}");
        Field("Expires", market.ExpirationTime is null ? None : $"{MomentWithZone(market.ExpirationTime)}");
        Field("Result", market.Result.Length == 0 ? None : market.Result);
        Field("Settlement", market.SettlementValue is null ? None : $"{Dollars(market.SettlementValue)} at {MomentWithZone(market.SettlementTime)}");
        Field("First seen", $"{MomentWithZone(market.FirstSeenAt)}");
        Field("Last seen", $"{MomentWithZone(market.LastSeenAt)}");
        rows.Add(new Text(string.Empty));
        if (tick is null)
        {
            rows.Add(new Markup("[bold]Latest tick[/]  none recorded"));
        }
        else
        {
            rows.Add(new Markup($"[bold]Latest tick[/]  {MomentWithZone(tick.Time, true)}"));
            rows.Add(new Markup($"  price {Dollars(tick.Price)}  bid {Dollars(tick.YesBid)} x {Count(tick.YesBidSize)}  ask {Dollars(tick.YesAsk)} x {Count(tick.YesAskSize)}"));
            rows.Add(new Markup($"  volume {Count(tick.Volume)}  open interest {Count(tick.OpenInterest)}"));
        }

        rows.Add(new Text(string.Empty));
        rows.Add(new Markup("[bold]Recent trades[/]"));
        rows.Add(trades.Count == 0 ? new Markup("  none recorded") : TradesTable(trades, width));
        return new Rows(rows);
    }

    public static IRenderable Ticks(TickPage page, int width)
    {
        var table = new TextTable()
            .Column($"Time ({ZoneName})").Column("Price", right: true).Column("Bid", right: true).Column("Ask", right: true)
            .Column("Bid size", right: true).Column("Ask size", right: true).Column("Volume", right: true).Column("Open interest", right: true);
        foreach (var tick in page.Ticks)
        {
            table.Row(Moment(tick.Time, true), Dollars(tick.Price), Dollars(tick.YesBid), Dollars(tick.YesAsk), Count(tick.YesBidSize), Count(tick.YesAskSize), Count(tick.Volume), Count(tick.OpenInterest));
        }

        return new Rows(table.Render(width), Footer(page.Ticks.Count, page.NextCursor));
    }

    public static IRenderable Metrics(MetricsDto metrics)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold]{Escape(metrics.Ticker)}[/]  as of {MomentWithZone(metrics.AsOf)}"),
            new Markup($"  last {Dollars(metrics.Last)}  ({(metrics.LastTime is null ? "no ticker update in the last 7 days" : $"at {MomentWithZone(metrics.LastTime, true)}")})"),
            new Markup($"  change 24h {Signed(metrics.Change24h)}   bid {Dollars(metrics.YesBid)}  ask {Dollars(metrics.YesAsk)}  spread {Dollars(metrics.Spread)}"),
            new Markup($"  volume 24h {Count(metrics.Volume24h)} in {metrics.TradeCount24h.ToString("N0", CultureInfo.InvariantCulture)} trades   VWAP {Dollars(metrics.Vwap24h)}   open interest {Count(metrics.OpenInterest)}"),
            new Markup($"  SMA 20h {Dollars(metrics.Sma20)}   SMA 50h {Dollars(metrics.Sma50)}   volatility 24h {Dollars(metrics.Volatility24h)}"),
        };
        if (metrics.Partial)
        {
            rows.Add(new Markup("[yellow]  partial: a gap overlaps the last 24 hours (or a candle used), so a figure may be incomplete[/]"));
        }

        return new Rows(rows);
    }

    /// <summary>A change with its sign: <c>+0.02</c>, <c>-0.01</c>, <c>0.00</c>.</summary>
    private static string Signed(string? value) => value is null ? None : value.StartsWith('-') || Dollars(value) is "0.00" ? Dollars(value) : "+" + Dollars(value);

    public static IRenderable Candles(CandlePage page, int width)
    {
        var table = new TextTable()
            .Column($"Time ({ZoneName})").Column("Open", right: true).Column("High", right: true).Column("Low", right: true).Column("Close", right: true)
            .Column("Volume", right: true).Column("Trades", right: true).Column("Bid", right: true).Column("Ask", right: true).Column("Open interest", right: true).Column("Note");
        foreach (var candle in page.Candles)
        {
            table.Row(
                Moment(candle.Time, true),
                Dollars(candle.Open),
                Dollars(candle.High),
                Dollars(candle.Low),
                Dollars(candle.Close),
                Count(candle.Volume),
                candle.TradeCount.ToString(CultureInfo.InvariantCulture),
                Dollars(candle.YesBid),
                Dollars(candle.YesAsk),
                Count(candle.OpenInterest),
                candle.Partial ? new Cell(PartialNote(candle), "yellow") : string.Empty);
        }

        return new Rows(table.Render(width), new Markup($"[grey]{page.Candles.Count} shown, {Escape(page.Interval)} candles ({Escape(page.Source)}). Yellow marks a bucket that overlaps a data gap.{(page.NextCursor is null ? string.Empty : $" More available: --cursor {Escape(page.NextCursor)}")}[/]"));
    }

    private static string PartialNote(CandleDto candle)
    {
        var what = candle.TradesPartial && candle.BookPartial ? "trades+book" : candle.TradesPartial ? "trades" : "book";
        var seconds = candle.GapSeconds is { } gap ? $" {gap.ToString(CultureInfo.InvariantCulture)}s" : string.Empty;
        return $"partial: {what}, {candle.GapKind ?? "gap"}{seconds}";
    }

    public static IRenderable Trades(TradePage page, int width) =>
        new Rows(TradesTable(page.Trades, width), Footer(page.Trades.Count, page.NextCursor));

    public static IRenderable Gaps(GapPage page, DateTime now, int width)
    {
        var table = new TextTable().Column("Id", right: true).Column("Kind").Column($"From ({ZoneName})").Column($"To ({ZoneName})").Column("Length", right: true).Column("Trades").Column("Detail");
        foreach (var gap in page.Gaps)
        {
            table.Row(
                gap.Id.ToString(CultureInfo.InvariantCulture),
                gap.Kind,
                Moment(gap.From),
                gap.To is null ? new Cell("open", "yellow") : Moment(gap.To),
                GapLength(gap, now),
                Recovery(gap.Backfill),
                gap.Detail ?? string.Empty);
        }

        return new Rows(table.Render(width), new Markup($"[grey]{page.Gaps.Count} shown. Data in a gap is lost; ticker updates cannot be recovered.[/]"));
    }

    /// <summary>How the trades of a gap are being recovered, in a few words.</summary>
    private static Cell Recovery(BackfillDto? backfill) => backfill switch
    {
        null => None,
        { Status: "done" } done => new Cell($"recovered {done.Added?.ToString("N0", CultureInfo.InvariantCulture) ?? None} of {done.Found?.ToString("N0", CultureInfo.InvariantCulture) ?? None}", "green"),
        { Status: "failed" } => new Cell("FAILED", "red"),
        { Status: "pending" or "running" } waiting => new Cell(waiting.Status, "yellow"),
        var other => other.Status,
    };

    /// <summary>How long a gap lasted (or has lasted so far, with a plus sign, when it is still open).</summary>
    private static string GapLength(GapDto gap, DateTime now)
    {
        if (!DateTimeOffset.TryParse(gap.From, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return None;
        }

        if (gap.To is null)
        {
            return Span(now - start.UtcDateTime) + "+";
        }

        return DateTimeOffset.TryParse(gap.To, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? Span(end.UtcDateTime - start.UtcDateTime) : None;
    }

    private static IRenderable TradesTable(IReadOnlyList<TradeDto> trades, int width)
    {
        var table = new TextTable().Column($"Time ({ZoneName})").Column("Yes", right: true).Column("No", right: true).Column("Count", right: true).Column("Taker").Column("Block");
        foreach (var trade in trades)
        {
            table.Row(
                Moment(trade.Time, true),
                Dollars(trade.YesPrice),
                Dollars(trade.NoPrice),
                Count(trade.Count),
                trade.TakerSide is null ? None : new Cell(trade.TakerSide, trade.TakerSide == "yes" ? "green" : "red"),
                trade.IsBlockTrade ? "block" : string.Empty);
        }

        return table.Render(width);
    }

    private static Markup Footer(int shown, string? cursor) => new Markup($"[grey]{shown} shown.{(cursor is null ? string.Empty : $" More available: --cursor {Escape(cursor)}")}[/]");

    private static string? StatusStyle(string status) => status switch
    {
        "active" => "green",
        "closed" or "determined" or "finalized" => "yellow",
        "unlisted" => "grey",
        _ => null,
    };

    private static string StatusMarkup(string status) => StatusStyle(status) is { } style ? $"[{style}]{Escape(status)}[/]" : Escape(status);

    private static string Stream(StreamDto stream) => stream.LastTime is null ? "[red]none in 24 h[/]" : Age(stream.AgeSeconds);

    private static string MarketCounts(IReadOnlyDictionary<string, long> byStatus) =>
        byStatus.Count == 0 ? "none" : string.Join("  |  ", byStatus.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value.ToString("N0", CultureInfo.InvariantCulture)}"));

    private static string BackupLine(BackupStatusDto? backup)
    {
        if (backup is null || !backup.Configured)
        {
            return "not configured";
        }

        var good = backup.LastGood is null ? "[red]no finished backup yet[/]" : $"last good {Escape(backup.LastGood)}, {Age(backup.LastGoodAgeSeconds)}";
        var failed = backup.Last is { } last && last.StartsWith("error", StringComparison.Ordinal) && (backup.LastGoodAt is null || string.CompareOrdinal(backup.LastAt, backup.LastGoodAt) >= 0)
            ? $"  |  [red]last attempt failed:[/] {Escape(last)}"
            : string.Empty;
        var verify = backup.LastVerify is null
            ? string.Empty
            : backup.LastVerify.StartsWith("error", StringComparison.Ordinal) ? $"  |  [red]proof failed:[/] {Escape(backup.LastVerify)}" : "  |  last proof by restore ok";
        return good + failed + verify;
    }

    private static string BackfillCounts(GapSummaryDto gaps) =>
        (gaps.BackfillPending, gaps.BackfillFailed) switch
        {
            (0, 0) => string.Empty,
            var (pending, failed) => $"  |  trade backfill: {pending} waiting{(failed > 0 ? $", [red]{failed} failed[/]" : string.Empty)}",
        };

    private static string GapKinds(IReadOnlyDictionary<string, long> byKind) =>
        byKind.Count == 0 ? string.Empty : "  (" + Escape(string.Join(", ", byKind.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}"))) + ")";
}
