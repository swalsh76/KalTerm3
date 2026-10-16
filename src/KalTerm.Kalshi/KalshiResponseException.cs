namespace KalTerm.Kalshi;

/// <summary>
/// Kalshi answered, but the body could not be turned into the expected model: a required field is missing, a value has
/// the wrong type, or a price or count is not exact at its scale. The message names what was being read.
/// </summary>
public sealed class KalshiResponseException : Exception
{
    public KalshiResponseException()
    {
    }

    public KalshiResponseException(string message)
        : base(message)
    {
    }

    public KalshiResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
