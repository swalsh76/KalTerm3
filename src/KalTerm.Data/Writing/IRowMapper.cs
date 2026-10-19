namespace KalTerm.Data.Writing;

/// <summary>Describes how a row type maps onto a table's columns.</summary>
/// <typeparam name="T">The row type.</typeparam>
public interface IRowMapper<in T>
{
    /// <summary>The destination table, schema-qualified.</summary>
    string Table { get; }

    /// <summary>The column names, in the order <see cref="GetValue"/> uses.</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>The CLR type of each column (the non-nullable type; NULLs are passed as <see langword="null"/>), same order as <see cref="Columns"/>.</summary>
    IReadOnlyList<Type> ColumnTypes { get; }

    /// <summary>The value of one column, or <see langword="null"/> for SQL NULL.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The index into <see cref="Columns"/>.</param>
    /// <returns>The value as the column's CLR type.</returns>
    object? GetValue(T row, int column);

    /// <summary>The row's timestamp (UTC), used to describe dropped ranges.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The row's time.</returns>
    DateTime Timestamp(T row);
}

/// <summary>Writes a batch of rows to the database in one atomic operation.</summary>
/// <typeparam name="T">The row type.</typeparam>
public interface IBatchSink<in T>
{
    /// <summary>Writes the whole batch or none of it. A failure must leave nothing behind, so a retry cannot duplicate rows.</summary>
    /// <param name="batch">The rows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the batch has committed.</returns>
    Task WriteAsync(IReadOnlyList<T> batch, CancellationToken cancellationToken);
}
