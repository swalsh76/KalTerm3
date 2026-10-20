using System.Globalization;
using KalTerm.Kalshi.Models;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using Spectre.Console;
using Spectre.Console.Rendering;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>How the account looks in a terminal: balance, positions, orders, fills, and what an order did. Numbers come from exact fixed-point values and are never rounded.</summary>
internal static class TradingViews
{
    /// <summary>What <c>orders</c> and <c>fills</c> print under the table: how to read the YES book.</summary>
    public const string OrdersNote = "BID = buy YES (or sell NO); ASK = sell YES (or buy NO at the NO price shown). `kterm amend --price` takes the YES price.";

    public static string Money(long micros) => RiskChecker.DollarsText(micros);

    public static string Money(KalTerm.Core.Money.Dollars? value) => value is { } dollars ? Money(dollars.Micros) : None;

    public static string Number(KalTerm.Core.Money.Contracts value) => RiskChecker.CountText(value.Hundredths);

    private static string Stamp(DateTimeOffset? moment, bool seconds = false) =>
        moment is { } at ? Moment(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)) : None;

    public static IRenderable Balance(AccountBalance balance, string? haltedReason)
    {
        var table = new TextTable().Column("Shard", right: true).Column("Available", right: true);
        foreach (var row in (balance.BalanceBreakdown ?? []).OrderBy(r => r.ExchangeIndex))
        {
            table.Row(row.ExchangeIndex.ToString(CultureInfo.InvariantCulture), Money(row.Balance.Micros));
        }

        table.Row("total", Money(balance.BalanceDollars.Micros));
        var parts = new List<IRenderable>();
        if (haltedReason is not null)
        {
            parts.Add(new Markup($"[bold red]TRADING HALTED ({Escape(haltedReason.Length == 0 ? "kill switch" : haltedReason)}): kterm resume[/]"));
        }

        parts.Add(new Markup("[bold]Balance[/]"));
        parts.Add(table.Render(100));
        parts.Add(new Text($"portfolio value: {Money(balance.PortfolioValue * 10_000)}"));
        return new Rows(parts);
    }

    private static string PositionLabel(long hundredths) =>
        hundredths == 0 ? "flat" : $"{RiskChecker.CountText(Math.Abs(hundredths))} {(hundredths > 0 ? "YES" : "NO")}";

    public static IRenderable Positions(IReadOnlyList<MarketPosition> positions)
    {
        if (positions.Count == 0)
        {
            return new Text("no open positions");
        }

        var table = new TextTable().Column("Ticker").Column("Shard", right: true).Column("Position", right: true).Column("Exposure", right: true).Column("Realized", right: true).Column("Fees", right: true);
        foreach (var p in positions)
        {
            table.Row(p.Ticker, p.ExchangeIndex.ToString(CultureInfo.InvariantCulture), PositionLabel(p.PositionFp.Hundredths), Money(p.MarketExposureDollars), Money(p.RealizedPnlDollars), Money(p.FeesPaidDollars));
        }

        return table.Render(120);
    }

    /// <summary>Orders as Kalshi's YES book holds them: a BID buys YES, an ASK sells YES (the same as buying NO at 1 - price), so both prices are shown. Kalshi's action and outcome labels on an order are not dependable, so they are not used.</summary>
    public static IRenderable Orders(IReadOnlyList<Order> orders, int width)
    {
        if (orders.Count == 0)
        {
            return new Text("no orders");
        }

        var table = new TextTable().Column("Order").Column("Ticker").Column("Book").Column("Yes", right: true).Column("No", right: true).Column("Filled", right: true).Column("Left", right: true).Column("Status").Column("Created");
        foreach (var o in orders)
        {
            table.Row(o.OrderId, o.Ticker, o.BookSide.Length == 0 ? None : o.BookSide.ToUpperInvariant(), Money(o.YesPriceDollars.Micros), Money(o.NoPriceDollars.Micros), Number(o.FillCountFp), Number(o.RemainingCountFp), o.Status, Stamp(o.CreatedTime));
        }

        return table.Render(width);
    }

    /// <summary>Fills on the YES book, like <see cref="Orders"/>: the book side and the two prices are dependable, the labels are not.</summary>
    public static IRenderable Fills(IReadOnlyList<Fill> fills, int width)
    {
        if (fills.Count == 0)
        {
            return new Text("no fills");
        }

        var table = new TextTable().Column("Time").Column("Ticker").Column("Book").Column("Count", right: true).Column("Yes", right: true).Column("No", right: true).Column("Fee", right: true).Column("Role");
        foreach (var f in fills)
        {
            table.Row(Stamp(f.CreatedTime), f.Ticker, f.BookSide.Length == 0 ? None : f.BookSide.ToUpperInvariant(), Number(f.CountFp), Money(f.YesPriceDollars.Micros), Money(f.NoPriceDollars.Micros), Money(f.FeeCost.Micros), f.IsTaker ? "taker" : "maker");
        }

        return table.Render(width);
    }

    public static IReadOnlyList<string> PlacementLines(Placement placement)
    {
        var lines = placement.Transfers.Select(t => $"moved {Money(t.AmountE6)} from shard {t.Source} to shard {t.Destination}").ToList();
        var created = placement.Created;
        lines.Add($"order {created.OrderId} placed{(created.Recovered ? " (found by looking after an unclear answer)" : string.Empty)}");
        lines.Add($"filled {RiskChecker.CountText(created.FillCountE2)}, resting {RiskChecker.CountText(created.RemainingCountE2)}; client id {placement.ClientOrderId}");
        return lines;
    }

    public static IReadOnlyList<string> AmendmentLines(Amendment amendment)
    {
        var lines = amendment.Transfers.Select(t => $"moved {Money(t.AmountE6)} from shard {t.Source} to shard {t.Destination}").ToList();
        lines.Add($"order {amendment.OrderId} amended");
        lines.Add($"resting {(amendment.Result.RemainingCountE2 is { } left ? RiskChecker.CountText(left) : None)}");
        return lines;
    }
}
