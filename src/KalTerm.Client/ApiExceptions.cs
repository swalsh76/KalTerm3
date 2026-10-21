using System.Net;

namespace KalTerm.Client;

/// <summary>The API answered, and the answer was an error (or was not the JSON it promises).</summary>
public sealed class ApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiException()
        : base("the API returned an error")
    {
        Code = "http_error";
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ApiException(string message)
        : base(message)
    {
        Code = "http_error";
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "http_error";
    }

    /// <summary>Creates the exception from an error response.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The API's error code, for example <c>invalid_parameter</c> or <c>not_found</c>.</param>
    /// <param name="fields">For <c>invalid_parameter</c>, which parameters were wrong.</param>
    public ApiException(HttpStatusCode status, string code, IReadOnlyList<string>? fields = null)
        : base(Describe(status, code, fields))
    {
        Status = status;
        Code = code;
        Fields = fields ?? [];
    }

    /// <summary>The HTTP status, when there was a response.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>The API's error code, or <c>http_error</c> / <c>bad_response</c> when the body was not the API's own.</summary>
    public string Code { get; }

    /// <summary>The parameters the API named as wrong.</summary>
    public IReadOnlyList<string> Fields { get; } = [];

    private static string Describe(HttpStatusCode status, string code, IReadOnlyList<string>? fields) =>
        fields is { Count: > 0 }
            ? $"the API refused the request ({(int)status} {code}): check {string.Join(", ", fields)}"
            : $"the API refused the request ({(int)status} {code})";
}

/// <summary>An API address that would send the token unencrypted over a network: plain http to a host that is not this computer.</summary>
public sealed class InsecureAddressException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InsecureAddressException()
        : base("the address would send your token unencrypted; use an https:// address")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public InsecureAddressException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public InsecureAddressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The API could not be reached at all: nothing is listening, the connection failed, or it did not answer in time.</summary>
public sealed class ApiUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiUnavailableException()
        : base("the API could not be reached")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ApiUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ApiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
