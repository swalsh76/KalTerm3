namespace KalTerm.Kalshi.Configuration;

/// <summary>Which Kalshi exchange to talk to. <see cref="Demo"/> is the default everywhere; production is an explicit opt-in.</summary>
public enum KalshiEnvironment
{
    Demo = 0,
    Production = 1,
}
