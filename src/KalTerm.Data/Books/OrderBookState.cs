using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Books;

/// <summary>One price level of a published order book: price at 10^-6 dollars, quantity at 10^-2 contracts.</summary>
/// <param name="PriceE6">The price.</param>
/// <param name="QuantityE2">The quantity.</param>
public readonly record struct BookLevel(long PriceE6, long QuantityE2);

/// <summary>The published form of one market's book (one row of <c>dbo.orderbook_state</c>).</summary>
/// <param name="MarketId">The market.</param>
/// <param name="UpdatedAt">When the book last changed (UTC).</param>
/// <param name="PublishedAt">When this row was last written (UTC).</param>
/// <param name="Stale">Messages were missed (or there is no book yet) and recovery has not finished.</param>
/// <param name="Approximate">Rebuilt from a REST snapshot.</param>
/// <param name="YesLevels">Yes bids, best first.</param>
/// <param name="NoLevels">No bids, best first.</param>
public sealed record BookState(int MarketId, DateTime UpdatedAt, DateTime PublishedAt, bool Stale, bool Approximate, IReadOnlyList<BookLevel> YesLevels, IReadOnlyList<BookLevel> NoLevels);

/// <summary>The text form of a list of levels: <c>price_e6:quantity_e2;price_e6:quantity_e2</c>.</summary>
public static class BookLevelText
{
    /// <summary>The most levels per side that are published.</summary>
    public const int MaxLevels = 100;

    /// <summary>Writes levels as text; at most <see cref="MaxLevels"/> are kept.</summary>
    /// <param name="levels">The levels, best first.</param>
    /// <returns>The text, empty for no levels.</returns>
    public static string Encode(IEnumerable<BookLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var text = new StringBuilder();
        foreach (var level in levels.Take(MaxLevels))
        {
            if (text.Length > 0)
            {
                text.Append(';');
            }

            text.Append(level.PriceE6.ToString(CultureInfo.InvariantCulture)).Append(':').Append(level.QuantityE2.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Reads levels back; text that is not in the form is refused.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The levels.</returns>
    /// <exception cref="FormatException">The text is not a list of levels.</exception>
    public static IReadOnlyList<BookLevel> Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var levels = new List<BookLevel>();
        foreach (var part in text.Split(';'))
        {
            var fields = part.Split(':');
            if (fields.Length != 2
                || !long.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var price)
                || !long.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var quantity))
            {
                throw new FormatException("a published order book level is not price:quantity");
            }

            levels.Add(new BookLevel(price, quantity));
        }

        return levels;
    }
}

/// <summary>Writes the published books.</summary>
public interface IBookStateStore
{
    /// <summary>Stores the books, replacing any earlier row of the same market, all in one transaction.</summary>
    /// <param name="books">The books.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when they are stored.</returns>
    Task SaveAsync(IReadOnlyList<BookState> books, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IBookStateStore"/> over <c>dbo.orderbook_state</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class BookStateStore(DataSource source) : IBookStateStore
{
    private const string Merge = """
        MERGE dbo.orderbook_state WITH (HOLDLOCK) AS t
        USING (VALUES (@market)) AS s (market_id) ON t.market_id = s.market_id
        WHEN MATCHED THEN UPDATE SET updated_at = @updated, published_at = @published, is_stale = @stale, is_approximate = @approximate, yes_levels = @yes, no_levels = @no
        WHEN NOT MATCHED THEN INSERT (market_id, updated_at, published_at, is_stale, is_approximate, yes_levels, no_levels)
            VALUES (@market, @updated, @published, @stale, @approximate, @yes, @no);
        """;

    /// <inheritdoc/>
    public async Task SaveAsync(IReadOnlyList<BookState> books, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        if (books.Count == 0)
        {
            return;
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(Merge, connection, transaction);
        var market = command.Parameters.Add("@market", System.Data.SqlDbType.Int);
        var updated = command.Parameters.Add("@updated", System.Data.SqlDbType.DateTime2);
        updated.Scale = 3;
        var published = command.Parameters.Add("@published", System.Data.SqlDbType.DateTime2);
        published.Scale = 3;
        var stale = command.Parameters.Add("@stale", System.Data.SqlDbType.Bit);
        var approximate = command.Parameters.Add("@approximate", System.Data.SqlDbType.Bit);
        var yes = command.Parameters.Add("@yes", System.Data.SqlDbType.VarChar, 8000);
        var no = command.Parameters.Add("@no", System.Data.SqlDbType.VarChar, 8000);
        foreach (var book in books)
        {
            market.Value = book.MarketId;
            updated.Value = book.UpdatedAt;
            published.Value = book.PublishedAt;
            stale.Value = book.Stale;
            approximate.Value = book.Approximate;
            yes.Value = BookLevelText.Encode(book.YesLevels);
            no.Value = BookLevelText.Encode(book.NoLevels);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
