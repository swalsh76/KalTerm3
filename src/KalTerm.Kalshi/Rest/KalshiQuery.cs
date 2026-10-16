using System.Globalization;
using System.Text;

namespace KalTerm.Kalshi.Rest;

/// <summary>
/// An immutable set of query-string parameters. A null value is dropped (so optional filters can be passed straight
/// through), booleans become <c>true</c>/<c>false</c>, numbers use the invariant culture, and names and values are URL-escaped.
/// Adding a name that is already present replaces it. Insertion order is kept.
/// </summary>
public sealed class KalshiQuery
{
    private readonly KeyValuePair<string, string>[] _items;

    private KalshiQuery(KeyValuePair<string, string>[] items)
    {
        _items = items;
    }

    /// <summary>No parameters.</summary>
    public static KalshiQuery Empty { get; } = new([]);

    /// <summary>The parameters as name/value pairs, unescaped, in insertion order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    public KalshiQuery With(string name, string? value) => value is null ? this : Set(name, value);

    public KalshiQuery With(string name, long? value) =>
        value is null ? this : Set(name, value.Value.ToString(CultureInfo.InvariantCulture));

    public KalshiQuery With(string name, decimal? value) =>
        value is null ? this : Set(name, value.Value.ToString(CultureInfo.InvariantCulture));

    public KalshiQuery With(string name, bool? value) =>
        value is null ? this : Set(name, value.Value ? "true" : "false");

    /// <summary>A moment as whole seconds since the Unix epoch, the form Kalshi's timestamp filters take.</summary>
    public KalshiQuery With(string name, DateTimeOffset? value) =>
        value is null ? this : Set(name, value.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>The escaped query string including the leading <c>?</c>, or empty when there are no parameters.</summary>
    internal string ToQueryString()
    {
        if (_items.Length == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder("?");
        for (var i = 0; i < _items.Length; i++)
        {
            if (i > 0)
            {
                text.Append('&');
            }

            text.Append(Uri.EscapeDataString(_items[i].Key)).Append('=').Append(Uri.EscapeDataString(_items[i].Value));
        }

        return text.ToString();
    }

    private KalshiQuery Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var list = _items.Where(i => !string.Equals(i.Key, name, StringComparison.Ordinal)).ToList();
        list.Add(new KeyValuePair<string, string>(name, value));
        return new KalshiQuery([.. list]);
    }
}
