using System.Globalization;

namespace KalTerm.Kalshi.Configuration;

/// <summary>
/// Validated, immutable Kalshi settings. <see cref="Default"/> is demo with the Basic-tier budgets. Production
/// needs an explicit <c>KALSHI_ENV=production</c> (or <see cref="KalshiEnvironment.Production"/> in code).
/// Anything invalid throws <see cref="KalshiConfigurationException"/> naming the setting.
/// </summary>
/// <remarks>
/// Variables read: <c>KALSHI_ENV</c>, <c>KALSHI_KEY_ID</c>, <c>KALSHI_PRIVATE_KEY_PATH</c>,
/// <c>KALSHI_READ_TOKENS_PER_SECOND</c>, <c>KALSHI_WRITE_TOKENS_PER_SECOND</c>, <c>KALSHI_RATE_LIMIT_MARGIN</c>.
/// An empty value counts as unset. There is no <c>.env</c> file support, by design.
/// </remarks>
public sealed record KalshiSettings
{
    /// <summary>Every setting is read from a variable with this prefix.</summary>
    public const string VariablePrefix = "KALSHI_";

    public const string EnvironmentVariable = VariablePrefix + "ENV";
    public const string KeyIdVariable = VariablePrefix + "KEY_ID";
    public const string PrivateKeyPathVariable = VariablePrefix + "PRIVATE_KEY_PATH";
    public const string ReadTokensVariable = VariablePrefix + "READ_TOKENS_PER_SECOND";
    public const string WriteTokensVariable = VariablePrefix + "WRITE_TOKENS_PER_SECOND";
    public const string RateLimitMarginVariable = VariablePrefix + "RATE_LIMIT_MARGIN";

    /// <summary>Basic-tier read budget in tokens per second.</summary>
    public const decimal DefaultReadTokensPerSecond = 200m;

    /// <summary>Basic-tier write budget in tokens per second.</summary>
    public const decimal DefaultWriteTokensPerSecond = 100m;

    /// <summary>The fraction of the budget actually used; the rest is margin against clock and jitter.</summary>
    public const decimal DefaultRateLimitMargin = 0.9m;

    private const int MaxBudgetDecimals = 6;   // the token bucket's resolution

    private static readonly string[] AllVariables =
    [
        EnvironmentVariable, KeyIdVariable, PrivateKeyPathVariable, ReadTokensVariable, WriteTokensVariable, RateLimitMarginVariable,
    ];

    private KalshiSettings(
        KalshiEnvironment environment,
        string? keyId,
        string? privateKeyPath,
        decimal readTokensPerSecond,
        decimal writeTokensPerSecond,
        decimal rateLimitMargin)
    {
        Environment = environment;
        KeyId = keyId;
        PrivateKeyPath = privateKeyPath;
        ReadTokensPerSecond = readTokensPerSecond;
        WriteTokensPerSecond = writeTokensPerSecond;
        RateLimitMargin = rateLimitMargin;
    }

    /// <summary>Demo, no credentials, Basic-tier budgets.</summary>
    public static KalshiSettings Default { get; } = Create();

    public KalshiEnvironment Environment { get; }

    /// <summary>The data key id, or <c>null</c> when none is configured.</summary>
    public string? KeyId { get; }

    /// <summary>Path of the data key's PEM file as configured (not expanded), or <c>null</c>.</summary>
    public string? PrivateKeyPath { get; }

    public decimal ReadTokensPerSecond { get; }

    public decimal WriteTokensPerSecond { get; }

    public decimal RateLimitMargin { get; }

    public bool IsProduction => Environment == KalshiEnvironment.Production;

    public Uri RestUrl => KalshiEndpoints.Rest(Environment);

    public Uri WebSocketUrl => KalshiEndpoints.WebSocket(Environment);

    /// <summary>Tokens per second the client may actually spend on reads: the budget times the margin.</summary>
    public decimal ReadBudget => ReadTokensPerSecond * RateLimitMargin;

    /// <summary>Tokens per second the client may actually spend on writes: the budget times the margin.</summary>
    public decimal WriteBudget => WriteTokensPerSecond * RateLimitMargin;

    public static KalshiSettings Create(
        KalshiEnvironment environment = KalshiEnvironment.Demo,
        string? keyId = null,
        string? privateKeyPath = null,
        decimal readTokensPerSecond = DefaultReadTokensPerSecond,
        decimal writeTokensPerSecond = DefaultWriteTokensPerSecond,
        decimal rateLimitMargin = DefaultRateLimitMargin)
    {
        if (!Enum.IsDefined(environment))
        {
            throw new KalshiConfigurationException($"unknown Kalshi environment: {environment}");
        }

        if (readTokensPerSecond <= 0)
        {
            throw new KalshiConfigurationException($"{ReadTokensVariable} must be greater than zero");
        }

        if (writeTokensPerSecond <= 0)
        {
            throw new KalshiConfigurationException($"{WriteTokensVariable} must be greater than zero");
        }

        if (rateLimitMargin <= 0 || rateLimitMargin > 1)
        {
            throw new KalshiConfigurationException($"{RateLimitMarginVariable} must be greater than zero and at most 1");
        }

        RequireRepresentable(ReadTokensVariable, readTokensPerSecond * rateLimitMargin);
        RequireRepresentable(WriteTokensVariable, writeTokensPerSecond * rateLimitMargin);

        return new KalshiSettings(
            environment,
            Clean(keyId),
            Clean(privateKeyPath),
            readTokensPerSecond,
            writeTokensPerSecond,
            rateLimitMargin);
    }

    /// <summary>Builds settings from <c>KALSHI_*</c> variables (names matched case-insensitively).</summary>
    public static KalshiSettings FromVariables(IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var values = new Dictionary<string, string?>(variables, StringComparer.OrdinalIgnoreCase);

        return Create(
            ParseEnvironment(Value(values, EnvironmentVariable)),
            Value(values, KeyIdVariable),
            Value(values, PrivateKeyPathVariable),
            ParseNumber(ReadTokensVariable, Value(values, ReadTokensVariable), DefaultReadTokensPerSecond),
            ParseNumber(WriteTokensVariable, Value(values, WriteTokensVariable), DefaultWriteTokensPerSecond),
            ParseNumber(RateLimitMarginVariable, Value(values, RateLimitMarginVariable), DefaultRateLimitMargin));
    }

    /// <summary>Builds settings from the process environment, or from <paramref name="getVariable"/> when given (tests).</summary>
    public static KalshiSettings FromEnvironment(Func<string, string?>? getVariable = null)
    {
        getVariable ??= System.Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllVariables)
        {
            values[name] = getVariable(name);
        }

        return FromVariables(values);
    }

    private static KalshiEnvironment ParseEnvironment(string? text)
    {
        if (text is null || text.Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            return KalshiEnvironment.Demo;   // unset means demo: the safe default
        }

        if (text.Equals("production", StringComparison.OrdinalIgnoreCase))
        {
            return KalshiEnvironment.Production;
        }

        throw new KalshiConfigurationException($"{EnvironmentVariable} must be 'demo' or 'production', got '{text}'");
    }

    private static decimal ParseNumber(string name, string? text, decimal fallback)
    {
        if (text is null)
        {
            return fallback;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            throw new KalshiConfigurationException($"{name} must be a positive decimal number such as 200 or 0.9, got '{text}'");
        }

        return number;
    }

    private static void RequireRepresentable(string name, decimal budget)
    {
        if (decimal.Round(budget, MaxBudgetDecimals) != budget)
        {
            throw new KalshiConfigurationException(
                $"{name} times the margin must have at most {MaxBudgetDecimals} decimal places, got {budget}");
        }
    }

    private static string? Value(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var text) ? Clean(text) : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
