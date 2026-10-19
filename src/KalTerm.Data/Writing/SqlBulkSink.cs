using System.Data;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Writing;

/// <summary>
/// An <see cref="IBatchSink{T}"/> that bulk-copies each batch into a table in one internal transaction. It keeps one
/// <see cref="DataTable"/> and reuses it, so it must be used by one writer loop at a time.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class SqlBulkSink<T> : IBatchSink<T>, IDisposable
{
    private readonly DataSource _source;
    private readonly IRowMapper<T> _mapper;
    private readonly DataTable _table = new();

    /// <summary>Creates the sink.</summary>
    /// <param name="source">Opens connections.</param>
    /// <param name="mapper">Maps rows to columns.</param>
    public SqlBulkSink(DataSource source, IRowMapper<T> mapper)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mapper);
        _source = source;
        _mapper = mapper;
        _table.Locale = System.Globalization.CultureInfo.InvariantCulture;
        if (mapper.Columns.Count != mapper.ColumnTypes.Count)
        {
            throw new ArgumentException("The mapper has a different number of columns and column types.", nameof(mapper));
        }

        for (var i = 0; i < mapper.Columns.Count; i++)
        {
            _table.Columns.Add(mapper.Columns[i], mapper.ColumnTypes[i]);
        }
    }

    /// <inheritdoc/>
    public async Task WriteAsync(IReadOnlyList<T> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        _table.Clear();
        var values = new object[_mapper.Columns.Count];
        foreach (var row in batch)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = _mapper.GetValue(row, i) ?? DBNull.Value;
            }

            _table.Rows.Add(values);
        }

        await using var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.UseInternalTransaction, externalTransaction: null)
        {
            DestinationTableName = _mapper.Table,
            BatchSize = 0, // the whole batch in one transaction: a failure leaves nothing behind
            BulkCopyTimeout = 60,
        };
        foreach (var column in _mapper.Columns)
        {
            copy.ColumnMappings.Add(column, column);
        }

        await copy.WriteToServerAsync(_table, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => _table.Dispose();
}
