using System.Text;
using System.Text.Json;

namespace KalTerm.Trading.Transport;

/// <summary>Receives a record of every write the trading client sends and of what came back. Nothing secret is ever passed to it: no key, no signature.</summary>
public interface ITradingAudit
{
    /// <summary>Records one event.</summary>
    /// <param name="at">When it happened.</param>
    /// <param name="what">What happened (for example <c>POST /portfolio/events/orders</c> or <c>create_order.unknown</c>).</param>
    /// <param name="reference">The client order id, order id or transfer id it concerns.</param>
    /// <param name="status">The HTTP status, or <see langword="null"/>; 0 is no answer.</param>
    /// <param name="detail">Anything else worth keeping.</param>
    void Record(DateTimeOffset at, string what, string reference, int? status, string? detail);
}

/// <summary>An audit log of JSON lines, appended and flushed to disk for each event, so a crash loses at most the line being written.</summary>
public sealed class JsonLinesAudit : ITradingAudit
{
    private readonly string _path;
    private readonly Lock _gate = new();

    /// <param name="path">The file; its folder is created.</param>
    public JsonLinesAudit(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }
    }

    /// <inheritdoc />
    public void Record(DateTimeOffset at, string what, string reference, int? status, string? detail)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("at", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("what", what);
            writer.WriteString("ref", reference);
            if (status is { } code)
            {
                writer.WriteNumber("status", code);
            }

            if (detail is not null)
            {
                writer.WriteString("detail", detail);
            }

            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        lock (_gate)
        {
            using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            file.Write(Encoding.UTF8.GetBytes(line));
            file.Flush(flushToDisk: true);
        }
    }
}
