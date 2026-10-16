namespace KalTerm.Kalshi.Auth;

/// <summary>Credentials are missing or unusable. The message says what is wrong and never contains key material.</summary>
public sealed class AuthException : Exception
{
    public AuthException()
    {
    }

    public AuthException(string message)
        : base(message)
    {
    }

    public AuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
