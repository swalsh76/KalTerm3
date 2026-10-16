using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Kalshi.Models;

/// <summary>An event: a group of related markets. Multivariate (combo) events come from their own endpoint.</summary>
/// <param name="EventTicker">The event ticker.</param>
/// <param name="SeriesTicker">The series it belongs to.</param>
/// <param name="Title">The event title.</param>
/// <param name="MutuallyExclusive">Whether at most one of its markets can resolve yes.</param>
/// <param name="SubTitle">An optional subtitle.</param>
/// <param name="StrikeDate">The date the strike applies to.</param>
/// <param name="StrikePeriod">The period the strike applies to.</param>
/// <param name="LastUpdatedTs">When the event last changed.</param>
/// <param name="Markets">Present only when the request asked for nested markets.</param>
public sealed record KalshiEvent(
    string EventTicker,
    string SeriesTicker,
    string Title,
    bool MutuallyExclusive,
    string SubTitle = "",
    DateTimeOffset? StrikeDate = null,
    string? StrikePeriod = null,
    DateTimeOffset? LastUpdatedTs = null,
    IReadOnlyList<Market>? Markets = null);

/// <summary>A series of recurring events, with its fee schedule.</summary>
/// <param name="Ticker">The series ticker.</param>
/// <param name="Title">The series title.</param>
/// <param name="Frequency">How often events recur.</param>
/// <param name="Category">The category.</param>
/// <param name="Tags">Optional tags.</param>
/// <param name="FeeType">How fees are charged, for example <c>quadratic</c>.</param>
/// <param name="FeeMultiplier">The fee multiplier, when the schedule has one.</param>
/// <param name="VolumeFp">Contracts traded in the series.</param>
/// <param name="LastUpdatedTs">When the series last changed.</param>
public sealed record Series(
    string Ticker,
    string Title,
    string Frequency,
    string Category,
    IReadOnlyList<string>? Tags = null,
    string? FeeType = null,
    FeeMultiplier? FeeMultiplier = null,
    Contracts? VolumeFp = null,
    DateTimeOffset? LastUpdatedTs = null);
