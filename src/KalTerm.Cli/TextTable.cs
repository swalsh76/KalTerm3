using Spectre.Console;
using Spectre.Console.Rendering;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>One cell: plain text and, optionally, a Spectre style such as <c>green</c> or <c>bold red</c>.</summary>
/// <param name="Text">The text, exactly as it should look (it is escaped here).</param>
/// <param name="Style">A Spectre style applied to the padded cell.</param>
internal readonly record struct Cell(string Text, string? Style = null)
{
    public static implicit operator Cell(string text) => new(text);
}

/// <summary>
/// A table drawn as aligned plain text: columns as wide as their widest cell, two spaces between them, a line of dashes under the header. Nothing is ever
/// squeezed: only the last column wraps, with a hanging indent, when a row is wider than the console. Plain ASCII, so it survives remoting and pipes.
/// </summary>
internal sealed class TextTable
{
    private const string Gap = "  ";
    private const string Indent = "  ";
    private const int MinimumLastColumn = 20;
    private const string Hanging = "      ";

    private readonly List<(string Header, bool Right)> _columns = [];
    private readonly List<Cell[]> _rows = [];

    public TextTable Column(string header, bool right = false)
    {
        _columns.Add((header, right));
        return this;
    }

    public TextTable Row(params Cell[] cells)
    {
        if (cells.Length != _columns.Count)
        {
            throw new ArgumentException("a row needs one cell for each column", nameof(cells));
        }

        _rows.Add(cells);
        return this;
    }

    /// <summary>
    /// The table for a console of the given width. When the short columns leave too little room for the last one, the last column moves under each
    /// row instead of being squeezed beside it.
    /// </summary>
    public IRenderable Render(int width)
    {
        var widths = _columns.Select((c, i) => Math.Max(c.Header.Length, _rows.Select(r => r[i].Text.Length).DefaultIfEmpty(0).Max())).ToArray();
        var last = _columns.Count - 1;
        var room = width - Indent.Length - widths.Take(last).Sum(w => w + Gap.Length);
        return room >= MinimumLastColumn ? Beside(widths, room) : Stacked(widths, width);
    }

    private Rows Stacked(int[] widths, int width)
    {
        var last = _columns.Count - 1;
        var detailWidth = Math.Max(MinimumLastColumn, width - Hanging.Length);
        var lines = new List<IRenderable>
        {
            new Markup("[bold]" + Join(_columns.Take(last).Select((c, i) => Pad(c.Header, widths[i], c.Right, false)).ToArray()) + "[/]"),
            new Markup(Indent + string.Join(Gap, widths.Take(last).Select(w => new string('-', w)))),
        };

        foreach (var row in _rows)
        {
            lines.Add(new Markup(Indent + string.Join(Gap, row.Take(last).Select((cell, i) => Styled(cell with { Text = Pad(cell.Text, widths[i], _columns[i].Right, false) })))));
            if (row[last].Text.Length > 0)
            {
                lines.AddRange(Wrap(row[last].Text, detailWidth).Select(piece => new Markup(Hanging + Styled(row[last] with { Text = piece }))));
            }
        }

        return new Rows(lines);
    }

    private Rows Beside(int[] widths, int room)
    {
        var last = _columns.Count - 1;
        var lastWidth = room;
        var lines = new List<IRenderable>
        {
            new Markup("[bold]" + Join(_columns.Select((c, i) => Pad(c.Header, widths[i], c.Right, i == last)).ToArray()) + "[/]"),
            new Markup(Indent + string.Join(Gap, widths.Select((w, i) => new string('-', i == last ? Math.Min(w, lastWidth) : w)))),
        };

        foreach (var row in _rows)
        {
            var parts = row.Select((cell, i) => i == last ? cell : cell with { Text = Pad(cell.Text, widths[i], _columns[i].Right, false) }).ToArray();
            var wrapped = Wrap(row[last].Text, lastWidth);
            var first = parts.Take(last).Select(Styled).Append(Styled(row[last] with { Text = wrapped[0] }));
            lines.Add(new Markup(Indent + string.Join(Gap, first)));
            var hanging = new string(' ', Indent.Length + widths.Take(last).Sum(w => w + Gap.Length));
            lines.AddRange(wrapped.Skip(1).Select(extra => new Markup(hanging + Styled(row[last] with { Text = extra }))));
        }

        return new Rows(lines);
    }

    private static string Join(string[] cells) => Indent + string.Join(Gap, cells.Select(Escape));

    private static string Pad(string text, int width, bool right, bool isLast) =>
        isLast ? text : right ? text.PadLeft(width) : text.PadRight(width);

    private static string Styled(Cell cell) => cell.Style is null ? Escape(cell.Text) : $"[{cell.Style}]{Escape(cell.Text)}[/]";

    /// <summary>Breaks text at spaces into lines of at most <paramref name="width"/> characters; a word longer than that is cut.</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = word;
            while (piece.Length > width)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                    line = string.Empty;
                }

                lines.Add(piece[..width]);
                piece = piece[width..];
            }

            if (line.Length == 0)
            {
                line = piece;
            }
            else if (line.Length + 1 + piece.Length <= width)
            {
                line += " " + piece;
            }
            else
            {
                lines.Add(line);
                line = piece;
            }
        }

        lines.Add(line);
        return lines;
    }
}
