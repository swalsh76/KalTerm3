using System.Globalization;
using System.Text.Json;

namespace KalTerm.Trading.Risk;

/// <summary>The risk file is wrong; the message names the key and the file.</summary>
public sealed class RiskConfigException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RiskConfigException()
        : base("the risk file is not valid")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public RiskConfigException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public RiskConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Loads the risk limits from <c>risk.json</c> in the owner's profile (<c>%APPDATA%\KalTerm</c>). The file is the only way to change a limit; there is no command-line override, so a limit cannot be loosened by
/// a typo in a command. A missing file means the tiny built-in defaults. Mistakes are errors, never silently ignored: an unknown key, a duplicate key, a negative value or a JSON fraction is refused and the
/// message names the key. Money is written as a string of dollars (<c>"1.00"</c>) and counts as a string or whole number of contracts (<c>"2"</c>, <c>"0.5"</c>), so a number never goes through binary rounding.
/// <code>
/// {
///   "limits": {
///     "max_order_count": "5", "max_position": "10", "max_order_notional": "1.00", "max_open_exposure": "3.00",
///     "max_daily_loss": "2.00", "max_open_orders": 3, "max_transfer_per_order": "2.00", "max_transfer_per_day": "5.00",
///     "snapshot_max_age_seconds": 10
///   },
///   "trading": { "production_enabled": false }
/// }
/// </code>
/// </summary>
public static class RiskPolicyLoader
{
    /// <summary>The file's name.</summary>
    public const string FileName = "risk.json";

    /// <summary>Where the file lives: <c>%APPDATA%\KalTerm\risk.json</c>.</summary>
    /// <returns>The path.</returns>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", FileName);

    /// <summary>Reads the limits from a file; the defaults when the file does not exist.</summary>
    /// <param name="path">The file; <see langword="null"/> for <see cref="DefaultPath"/>.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="RiskConfigException">The file cannot be read or is wrong.</exception>
    public static RiskPolicy Load(string? path = null)
    {
        var target = path ?? DefaultPath();
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(target);
        }
        catch (FileNotFoundException)
        {
            return new RiskPolicy();
        }
        catch (DirectoryNotFoundException)
        {
            return new RiskPolicy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RiskConfigException($"cannot read {target}: {ex.Message}", ex);
        }

        return Parse(System.Text.Encoding.UTF8.GetString(raw), target);
    }

    /// <summary>Reads the limits from JSON text.</summary>
    /// <param name="json">The text.</param>
    /// <param name="source">The file's name, for messages.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="RiskConfigException">The text is not valid or names an unknown or unusable key.</exception>
    public static RiskPolicy Parse(string json, string source = FileName)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new RiskConfigException($"{source} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RiskConfigException($"{source}: the top level must be an object");
            }

            var policy = new RiskPolicy();
            foreach (var section in Properties(document.RootElement, source))
            {
                if (section.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new RiskConfigException($"{source}: {section.Name} must be an object");
                }

                policy = section.Name switch
                {
                    "limits" => Limits(policy, section.Value, source),
                    "trading" => Trading(policy, section.Value, source),
                    _ => throw new RiskConfigException($"{source}: unknown section '{section.Name}'"),
                };
            }

            return policy;
        }
    }

    private static RiskPolicy Limits(RiskPolicy policy, JsonElement limits, string source)
    {
        foreach (var property in Properties(limits, source))
        {
            var key = property.Name;
            policy = key switch
            {
                "max_order_notional" => policy with { MaxOrderNotionalE6 = Scaled(key, property.Value, 6) },
                "max_open_exposure" => policy with { MaxOpenExposureE6 = Scaled(key, property.Value, 6) },
                "max_daily_loss" => policy with { MaxDailyLossE6 = Scaled(key, property.Value, 6) },
                "max_transfer_per_order" => policy with { MaxTransferPerOrderE6 = Scaled(key, property.Value, 6) },
                "max_transfer_per_day" => policy with { MaxTransferPerDayE6 = Scaled(key, property.Value, 6) },
                "max_order_count" => policy with { MaxOrderCountE2 = Scaled(key, property.Value, 2) },
                "max_position" => policy with { MaxPositionE2 = Scaled(key, property.Value, 2) },
                "max_open_orders" => policy with { MaxOpenOrders = Whole(key, property.Value) },
                "snapshot_max_age_seconds" => policy with { SnapshotMaxAgeSeconds = Whole(key, property.Value) },
                _ => throw new RiskConfigException($"{source}: unknown limit '{key}'"),
            };
        }

        return policy;
    }

    private static RiskPolicy Trading(RiskPolicy policy, JsonElement trading, string source)
    {
        foreach (var property in Properties(trading, source))
        {
            if (property.Name != "production_enabled")
            {
                throw new RiskConfigException($"{source}: unknown setting '{property.Name}'");
            }

            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new RiskConfigException("production_enabled: must be true or false");
            }

            policy = policy with { ProductionEnabled = property.Value.GetBoolean() };
        }

        return policy;
    }

    /// <summary>The properties of an object, refusing a name that appears twice (JSON would let the last one win silently).</summary>
    private static List<JsonProperty> Properties(JsonElement element, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<JsonProperty>();
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new RiskConfigException($"{source}: '{property.Name}' appears twice");
            }

            list.Add(property);
        }

        return list;
    }

    /// <summary>A non-negative decimal (a string, or a whole JSON number) as an integer scaled by <c>10^places</c>.</summary>
    private static long Scaled(string key, JsonElement value, int places)
    {
        string text;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var whole) && !value.GetRawText().Contains('.', StringComparison.Ordinal) && !value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase):
                text = whole.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                throw new RiskConfigException($"{key}: write it as a string such as \"1.00\", not {value.GetRawText()}");
        }

        if (text.StartsWith('+') || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            throw new RiskConfigException($"{key}: '{text}' is not a number");
        }

        if (number < 0)
        {
            throw new RiskConfigException($"{key}: must be zero or more, not '{text}'");
        }

        var scaled = number * (places == 2 ? 100m : 1_000_000m);
        if (scaled != decimal.Truncate(scaled))
        {
            throw new RiskConfigException($"{key}: at most {places} decimal places, not '{text}'");
        }

        if (scaled > long.MaxValue)
        {
            throw new RiskConfigException($"{key}: '{text}' is too large");
        }

        return (long)scaled;
    }

    private static int Whole(string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0
            || value.GetRawText().Contains('.', StringComparison.Ordinal) || value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            throw new RiskConfigException($"{key}: must be a whole number, zero or more");
        }

        return number;
    }
}
