using System.Text.Json;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Kalshi.Tests.Live;

/// <summary>
/// M6 slice 6.1: what the account endpoints look like, measured with the read-only key and GET only. It prints the NAMES and KINDS of fields (and how many items a list holds), never a balance, a
/// price, a count or an id: the shapes are what the models and the fake exchange need, and the account's values are nobody's business but the owner's.
/// </summary>
public sealed class LiveAccountShapeTests
{
    private const string Why = LiveSupport.Why;

    private static readonly (string Path, (string Name, object Value)[] Query)[] Probes =
    [
        ("/portfolio/balance", []),
        ("/portfolio/positions", [("limit", 5)]),
        ("/portfolio/orders", [("limit", 5)]),
        ("/portfolio/fills", [("limit", 5)]),
        ("/portfolio/settlements", [("limit", 5)]),
        ("/portfolio/events/orders", [("limit", 5)]),
        ("/account/endpoint_costs", []),
        ("/account/limits", []),
    ];

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheShapesOfTheAccountEndpoints()
    {
        var (settings, signer) = LiveSupport.Credentials();
        using (signer)
        using (var client = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
        {
            foreach (var (path, parameters) in Probes)
            {
                var query = KalshiQuery.Empty;
                foreach (var (name, value) in parameters)
                {
                    query = query.With(name, (int)value);
                }

                try
                {
                    var body = await client.GetStringAsync(path, parameters.Length == 0 ? null : query, TestContext.Current.CancellationToken);
                    using var document = JsonDocument.Parse(body);
                    LiveSupport.Say($"GET {path}: HTTP 200");
                    Describe(document.RootElement, "  ", 0);
                }
                catch (KalshiApiException ex)
                {
                    LiveSupport.Say($"GET {path}: HTTP {ex.StatusCode}");
                }
            }
        }
    }

    /// <summary>Names and kinds only. A list shows its length and the shape of its first item; nothing a person would call a value is printed.</summary>
    private static void Describe(JsonElement element, string indent, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var kind = Kind(property.Value);
                    LiveSupport.Say($"{indent}{property.Name}: {kind}");
                    if (depth < 3 && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Describe(property.Value, indent + "  ", depth + 1);
                    }
                }

                break;
            case JsonValueKind.Array when element.GetArrayLength() > 0 && depth < 4:
                Describe(element[0], indent + "  [0] ", depth + 1);
                break;
        }
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => $"array({value.GetArrayLength()})",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        _ => "object",
    };
}
