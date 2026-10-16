namespace KalTerm.Kalshi.Configuration;

/// <summary>A Kalshi setting is missing, malformed or out of range. The message names the setting.</summary>
public sealed class KalshiConfigurationException : Exception
{
    public KalshiConfigurationException()
    {
    }

    public KalshiConfigurationException(string message)
        : base(message)
    {
    }

    public KalshiConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
