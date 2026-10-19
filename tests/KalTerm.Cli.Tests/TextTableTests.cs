using Spectre.Console.Testing;

namespace KalTerm.Cli.Tests;

public sealed class TextTableTests
{
    private static string Draw(TextTable table, int width)
    {
        using var console = new TestConsole().Width(width);
        console.Write(table.Render(width));
        return console.Output.Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsAreAsWideAsTheirWidestCellAndNumbersAlignRight()
    {
        var table = new TextTable().Column("Name").Column("Count", right: true).Column("Note");
        table.Row("a", "5", "first");
        table.Row("longer name", "1,234", "second");

        var lines = Draw(table, 80).Split('\n');

        Assert.Equal("  Name         Count  Note", lines[0]);
        Assert.Equal("  -----------  -----  ------", lines[1]);
        Assert.Equal("  a                5  first", lines[2]);
        Assert.Equal("  longer name  1,234  second", lines[3]);
    }

    [Fact]
    public void AnEmptyTableStillShowsItsHeader()
    {
        var lines = Draw(new TextTable().Column("A").Column("B"), 80).Split('\n');

        Assert.Equal("  A  B", lines[0]);
        Assert.Equal("  -  -", lines[1]);
    }

    [Fact]
    public void OnlyTheLastColumnWrapsAndItsContinuationsHangUnderIt()
    {
        var table = new TextTable().Column("Id", right: true).Column("Detail");
        table.Row("12", "one two three four five six seven");

        var lines = Draw(table, 30).Split('\n');

        Assert.Equal("  12  one two three four five", lines[2]);
        Assert.Equal("      six seven", lines[3]);
        Assert.All(lines, line => Assert.True(line.Length <= 30));
    }

    [Fact]
    public void AWordLongerThanTheLineIsCutRatherThanLost()
    {
        var table = new TextTable().Column("A").Column("Detail");
        table.Row("x", new string('w', 45));

        var output = Draw(table, 28);

        Assert.Equal(45, output.Count(c => c == 'w'));
        Assert.All(output.Split('\n'), line => Assert.True(line.Length <= 28));
    }

    [Fact]
    public void ANarrowConsoleWrapsTheDetailAndKeepsTheShortColumnsWhole()
    {
        var table = new TextTable().Column("Id").Column("Kind").Column("Detail");
        table.Row("123456", "ws_reconnected", "some words that need room");

        var lines = Draw(table, 50).Split('\n');

        Assert.Equal("  123456  ws_reconnected  some words that need", lines[2]);
        Assert.Equal("                          room", lines[3]);
    }

    [Fact]
    public void TextWithBracketsIsPrintedLiterallyAndAStyleDoesNotChangeTheText()
    {
        var table = new TextTable().Column("A").Column("B");
        table.Row(new Cell("[red]x[/]", "green"), "[/] odd [");

        var output = Draw(table, 80);

        Assert.Contains("[red]x[/]  [/] odd [", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowMustHaveOneCellForEachColumn()
    {
        var table = new TextTable().Column("A").Column("B");

        Assert.Throws<ArgumentException>(() => table.Row("only one"));
    }

    [Fact]
    public void WhenTheShortColumnsLeaveNoRoomTheLastColumnMovesUnderEachRow()
    {
        var table = new TextTable().Column("Kind").Column("When").Column("Detail");
        table.Row("ws_reconnected", "2026-10-19 04:33:49", "a fairly long detail that must wrap under the row nicely");
        table.Row("ws_lost", "2026-10-19 04:40:46", string.Empty);

        var lines = Draw(table, 40).Split('\n');

        Assert.Equal("  Kind            When", lines[0].TrimEnd());
        Assert.Equal("  ws_reconnected  2026-10-19 04:33:49", lines[2].TrimEnd());
        Assert.Equal("      a fairly long detail that must", lines[3].TrimEnd());
        Assert.Equal("      wrap under the row nicely", lines[4].TrimEnd());
        Assert.Equal("  ws_lost         2026-10-19 04:40:46", lines[5].TrimEnd());
        Assert.All(lines, line => Assert.True(line.Length <= 40));
    }
}
