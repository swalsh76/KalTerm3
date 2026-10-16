namespace KalTerm.Kalshi.Rest;

/// <summary>A request other than GET was attempted through the read-only client. Nothing was sent.</summary>
public sealed class ReadOnlyViolationException : Exception
{
    public ReadOnlyViolationException()
    {
    }

    public ReadOnlyViolationException(string message)
        : base(message)
    {
    }

    public ReadOnlyViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
