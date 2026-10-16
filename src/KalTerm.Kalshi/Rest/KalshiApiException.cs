namespace KalTerm.Kalshi.Rest;

/// <summary>Kalshi answered with an HTTP status other than 200 (after any retries were used up).</summary>
public sealed class KalshiApiException : Exception
{
    private const int MaxBodyInMessage = 200;

    public KalshiApiException()
    {
    }

    public KalshiApiException(string message)
        : base(message)
    {
    }

    public KalshiApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public KalshiApiException(int statusCode, string body)
        : base($"Kalshi API error {statusCode}: {Shorten(body)}")
    {
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The whole response body, which the message shortens.</summary>
    public string Body { get; } = string.Empty;

    private static string Shorten(string body) => body.Length <= MaxBodyInMessage ? body : body[..MaxBodyInMessage];
}
