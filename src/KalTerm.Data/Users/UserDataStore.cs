using Dapper;
using KalTerm.Data.Alerts;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Users;

/// <summary>What a request about a user's own data came to.</summary>
public enum StoreOutcome
{
    /// <summary>It was done (or was already so).</summary>
    Ok,

    /// <summary>There is no such watchlist, alert or entry for this user.</summary>
    NotFound,

    /// <summary>There is no such market.</summary>
    MarketNotFound,

    /// <summary>The user already has one by that name.</summary>
    Exists,

    /// <summary>The user is at a limit.</summary>
    LimitReached,
}

/// <summary>A watchlist in a list of them.</summary>
/// <param name="Name">The name.</param>
/// <param name="Markets">How many markets it holds.</param>
/// <param name="CreatedAt">When it was made (UTC).</param>
public sealed record WatchlistSummary(string Name, int Markets, DateTime CreatedAt);

/// <summary>A watchlist and its markets.</summary>
/// <param name="Name">The name.</param>
/// <param name="CreatedAt">When it was made (UTC).</param>
/// <param name="Tickers">Its markets, in ticker order.</param>
public sealed record WatchlistDetail(string Name, DateTime CreatedAt, IReadOnlyList<string> Tickers);

/// <summary>A user's alert.</summary>
/// <param name="AlertId">The alert.</param>
/// <param name="Ticker">The market it watches.</param>
/// <param name="Kind">What it watches for.</param>
/// <param name="Threshold">The threshold in the unit of the kind.</param>
/// <param name="CooldownSeconds">The least time between two fires.</param>
/// <param name="Paused">Whether it is paused.</param>
/// <param name="CreatedAt">When it was made (UTC).</param>
/// <param name="LastEvaluatedAt">When it was last judged (UTC).</param>
/// <param name="LastFiredAt">When it last fired (UTC).</param>
/// <summary>A saved layout in a list.</summary>
/// <param name="Name">Its name.</param>
/// <param name="UpdatedAt">When it was last saved (UTC).</param>
public sealed record LayoutSummary(string Name, DateTime UpdatedAt);

/// <summary>A saved layout.</summary>
/// <param name="Name">Its name.</param>
/// <param name="UpdatedAt">When it was last saved (UTC).</param>
/// <param name="Body">The JSON object the terminal saved; the server does not read it.</param>
public sealed record LayoutDetail(string Name, DateTime UpdatedAt, string Body);

public sealed record UserAlert(int AlertId, string Ticker, AlertKind Kind, long Threshold, int CooldownSeconds, bool Paused, DateTime CreatedAt, DateTime? LastEvaluatedAt, DateTime? LastFiredAt);

/// <summary>An alert that fired.</summary>
/// <param name="FireId">The fire; they only grow, so a client keeps the last one it saw.</param>
/// <param name="AlertId">The alert.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Threshold">The threshold at the time.</param>
/// <param name="Value">What was observed.</param>
/// <param name="FiredAt">When (UTC).</param>
public sealed record UserFire(long FireId, int AlertId, string Ticker, AlertKind Kind, long Threshold, long Value, DateTime FiredAt);

/// <summary>A user's own data: watchlists, alerts, the alerts that fired and settings. Every call is scoped to one user id, and nothing can reach another user's rows.</summary>
public interface IUserDataStore
{
    /// <summary>The user's watchlists.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The watchlists, by name.</returns>
    Task<IReadOnlyList<WatchlistSummary>> ListWatchlistsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>One watchlist and its markets.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The watchlist, or <see langword="null"/> when there is none.</returns>
    Task<WatchlistDetail?> GetWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>Makes an empty watchlist.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The name.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/>, <see cref="StoreOutcome.Exists"/> or <see cref="StoreOutcome.LimitReached"/>.</returns>
    Task<StoreOutcome> CreateWatchlistAsync(int userId, string name, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Deletes a watchlist with its markets.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/> or <see cref="StoreOutcome.NotFound"/>.</returns>
    Task<StoreOutcome> DeleteWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>Adds a market to a watchlist (already there is fine).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The watchlist.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/>, <see cref="StoreOutcome.NotFound"/> (no such watchlist), <see cref="StoreOutcome.MarketNotFound"/> or <see cref="StoreOutcome.LimitReached"/>.</returns>
    Task<StoreOutcome> AddMarketAsync(int userId, string name, string ticker, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Takes a market off a watchlist.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The watchlist.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/> or <see cref="StoreOutcome.NotFound"/> (no such watchlist or the market is not on it).</returns>
    Task<StoreOutcome> RemoveMarketAsync(int userId, string name, string ticker, CancellationToken cancellationToken = default);

    /// <summary>The markets of a watchlist, as ids (for filtering the screener).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The watchlist.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The market ids, or <see langword="null"/> when there is no such watchlist.</returns>
    Task<IReadOnlyList<int>?> WatchlistMarketIdsAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>The user's alerts.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The alerts, oldest first.</returns>
    Task<IReadOnlyList<UserAlert>> ListAlertsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Makes an alert.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="kind">What to watch for.</param>
    /// <param name="threshold">The threshold (already checked with <see cref="AlertKinds.IsValidThreshold"/>).</param>
    /// <param name="cooldownSeconds">The least time between two fires.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome and, when it is <see cref="StoreOutcome.Ok"/>, the new alert's id.</returns>
    Task<(StoreOutcome Outcome, int? AlertId)> AddAlertAsync(int userId, string ticker, AlertKind kind, long threshold, int cooldownSeconds, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Deletes an alert with its fires.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="alertId">The alert.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/> or <see cref="StoreOutcome.NotFound"/>.</returns>
    Task<StoreOutcome> DeleteAlertAsync(int userId, int alertId, CancellationToken cancellationToken = default);

    /// <summary>Pauses or resumes an alert. Resuming makes it judge its condition afresh, so it does not fire for what became true while it was paused.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="alertId">The alert.</param>
    /// <param name="paused">The new state.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/> or <see cref="StoreOutcome.NotFound"/>.</returns>
    Task<StoreOutcome> SetAlertPausedAsync(int userId, int alertId, bool paused, CancellationToken cancellationToken = default);

    /// <summary>The user's alerts that fired.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="afterFireId">Only fires with a larger id, oldest first; <see langword="null"/> for the newest <paramref name="limit"/> (still oldest first).</param>
    /// <param name="limit">How many, 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The fires.</returns>
    Task<IReadOnlyList<UserFire>> ReadFiresAsync(int userId, long? afterFireId, int limit, CancellationToken cancellationToken = default);

    /// <summary>The layouts the user saved, by name.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The layouts, without their bodies.</returns>
    Task<IReadOnlyList<LayoutSummary>> ListLayoutsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>One layout of the user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The layout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The layout, or <see langword="null"/> when there is none by that name.</returns>
    Task<LayoutDetail?> GetLayoutAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>Saves a layout: makes it, or replaces the one with that name.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The layout.</param>
    /// <param name="body">A JSON object.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/>, or <see cref="StoreOutcome.LimitReached"/> when the user has the most layouts and this one is new.</returns>
    Task<StoreOutcome> PutLayoutAsync(int userId, string name, string body, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Deletes a layout.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The layout.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="StoreOutcome.Ok"/> or <see cref="StoreOutcome.NotFound"/>.</returns>
    Task<StoreOutcome> DeleteLayoutAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>A setting of the user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The setting.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when it is not set.</returns>
    Task<string?> GetSettingAsync(int userId, string name, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears (<see langword="null"/>) a setting of the user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="name">The setting.</param>
    /// <param name="value">The value, or <see langword="null"/> to clear it.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task SetSettingAsync(int userId, string name, string? value, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IUserDataStore"/> over the user tables. Every statement is filtered by the user id it is given.</summary>
/// <param name="source">Opens connections.</param>
public sealed class UserDataStore(DataSource source) : IUserDataStore
{
    /// <summary>The most watchlists one user can have.</summary>
    public const int MaxWatchlists = 20;

    /// <summary>The most markets one watchlist can hold.</summary>
    public const int MaxMarketsPerWatchlist = 500;

    /// <summary>The most alerts one user can have.</summary>
    public const int MaxAlerts = 200;

    /// <summary>The most layouts one user can have.</summary>
    public const int MaxLayouts = 20;

    private const int MaxFires = 1_000;
    private const int UniqueViolation = 2601;
    private const int UniqueConstraint = 2627;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WatchlistSummary>> ListWatchlistsAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Name, int Markets, DateTime CreatedAt)>(Command(
            "SELECT w.name AS Name, (SELECT COUNT(*) FROM dbo.watchlist_market wm WHERE wm.watchlist_id = w.watchlist_id) AS Markets, w.created_at AS CreatedAt FROM dbo.watchlist w WHERE w.user_id = @userId ORDER BY w.name",
            new { userId },
            cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new WatchlistSummary(r.Name, r.Markets, Utc(r.CreatedAt)))];
    }

    /// <inheritdoc/>
    public async Task<WatchlistDetail?> GetWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var list = await connection.QuerySingleOrDefaultAsync<(int Id, DateTime CreatedAt)?>(Command(
            "SELECT watchlist_id AS Id, created_at AS CreatedAt FROM dbo.watchlist WHERE user_id = @userId AND name = @name",
            new { userId, name = Ansi(name, 64) },
            cancellationToken)).ConfigureAwait(false);
        if (list is not { } found)
        {
            return null;
        }

        var tickers = await connection.QueryAsync<string>(Command(
            "SELECT m.ticker FROM dbo.watchlist_market wm JOIN dbo.market m ON m.market_id = wm.market_id WHERE wm.watchlist_id = @id ORDER BY m.ticker",
            new { id = found.Id },
            cancellationToken)).ConfigureAwait(false);
        return new WatchlistDetail(name, Utc(found.CreatedAt), [.. tickers]);
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> CreateWatchlistAsync(int userId, string name, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM dbo.watchlist WHERE user_id = @userId", new { userId }, cancellationToken)).ConfigureAwait(false) >= MaxWatchlists)
        {
            return StoreOutcome.LimitReached;
        }

        try
        {
            await connection.ExecuteAsync(Command("INSERT dbo.watchlist (user_id, name, created_at) VALUES (@userId, @name, @now)", new { userId, name = Ansi(name, 64), now }, cancellationToken)).ConfigureAwait(false);
            return StoreOutcome.Ok;
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or UniqueConstraint)
        {
            return StoreOutcome.Exists;
        }
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> DeleteWatchlistAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync(Command("DELETE dbo.watchlist WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 64) }, cancellationToken)).ConfigureAwait(false);
        return deleted > 0 ? StoreOutcome.Ok : StoreOutcome.NotFound;
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> AddMarketAsync(int userId, string name, string ticker, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var listId = await connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT watchlist_id FROM dbo.watchlist WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 64) }, cancellationToken)).ConfigureAwait(false);
        if (listId is not { } id)
        {
            return StoreOutcome.NotFound;
        }

        var marketId = await connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT market_id FROM dbo.market WHERE ticker = @ticker", new { ticker = Ansi(ticker, 128) }, cancellationToken)).ConfigureAwait(false);
        if (marketId is not { } market)
        {
            return StoreOutcome.MarketNotFound;
        }

        if (await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM dbo.watchlist_market WHERE watchlist_id = @id AND market_id = @market", new { id, market }, cancellationToken)).ConfigureAwait(false) > 0)
        {
            return StoreOutcome.Ok;
        }

        if (await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM dbo.watchlist_market WHERE watchlist_id = @id", new { id }, cancellationToken)).ConfigureAwait(false) >= MaxMarketsPerWatchlist)
        {
            return StoreOutcome.LimitReached;
        }

        try
        {
            await connection.ExecuteAsync(Command("INSERT dbo.watchlist_market (watchlist_id, market_id, added_at) VALUES (@id, @market, @now)", new { id, market, now }, cancellationToken)).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or UniqueConstraint)
        {
            // Added by a concurrent request: the same result.
        }

        return StoreOutcome.Ok;
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> RemoveMarketAsync(int userId, string name, string ticker, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var removed = await connection.ExecuteAsync(Command(
            """
            DELETE wm FROM dbo.watchlist_market wm
            JOIN dbo.watchlist w ON w.watchlist_id = wm.watchlist_id
            JOIN dbo.market m ON m.market_id = wm.market_id
            WHERE w.user_id = @userId AND w.name = @name AND m.ticker = @ticker
            """,
            new { userId, name = Ansi(name, 64), ticker = Ansi(ticker, 128) },
            cancellationToken)).ConfigureAwait(false);
        return removed > 0 ? StoreOutcome.Ok : StoreOutcome.NotFound;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>?> WatchlistMarketIdsAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var listId = await connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT watchlist_id FROM dbo.watchlist WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 64) }, cancellationToken)).ConfigureAwait(false);
        if (listId is not { } id)
        {
            return null;
        }

        return [.. await connection.QueryAsync<int>(Command("SELECT market_id FROM dbo.watchlist_market WHERE watchlist_id = @id ORDER BY market_id", new { id }, cancellationToken)).ConfigureAwait(false)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UserAlert>> ListAlertsAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AlertRow>(Command(
            """
            SELECT a.alert_id AS AlertId, m.ticker AS Ticker, a.kind AS Kind, a.threshold AS Threshold, a.cooldown_s AS CooldownSeconds, a.paused AS Paused,
                   a.created_at AS CreatedAt, a.last_evaluated_at AS LastEvaluatedAt, a.last_fired_at AS LastFiredAt
            FROM dbo.alert a JOIN dbo.market m ON m.market_id = a.market_id WHERE a.user_id = @userId ORDER BY a.alert_id
            """,
            new { userId },
            cancellationToken)).ConfigureAwait(false);
        return [.. rows
            .Select(r => AlertKinds.TryParse(r.Kind, out var kind)
                ? new UserAlert(r.AlertId, r.Ticker, kind, r.Threshold, r.CooldownSeconds, r.Paused, Utc(r.CreatedAt), r.LastEvaluatedAt is { } e ? Utc(e) : null, r.LastFiredAt is { } f ? Utc(f) : null)
                : null)
            .OfType<UserAlert>()];
    }

    /// <inheritdoc/>
    public async Task<(StoreOutcome Outcome, int? AlertId)> AddAlertAsync(int userId, string ticker, AlertKind kind, long threshold, int cooldownSeconds, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var marketId = await connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT market_id FROM dbo.market WHERE ticker = @ticker", new { ticker = Ansi(ticker, 128) }, cancellationToken)).ConfigureAwait(false);
        if (marketId is not { } market)
        {
            return (StoreOutcome.MarketNotFound, null);
        }

        if (await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM dbo.alert WHERE user_id = @userId", new { userId }, cancellationToken)).ConfigureAwait(false) >= MaxAlerts)
        {
            return (StoreOutcome.LimitReached, null);
        }

        var id = await connection.ExecuteScalarAsync<int>(Command(
            "INSERT dbo.alert (user_id, market_id, kind, threshold, cooldown_s, created_at) OUTPUT INSERTED.alert_id VALUES (@userId, @market, @kind, @threshold, @cooldownSeconds, @now)",
            new { userId, market, kind = Ansi(AlertKinds.Name(kind), 24), threshold, cooldownSeconds, now },
            cancellationToken)).ConfigureAwait(false);
        return (StoreOutcome.Ok, id);
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> DeleteAlertAsync(int userId, int alertId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync(Command("DELETE dbo.alert WHERE user_id = @userId AND alert_id = @alertId", new { userId, alertId }, cancellationToken)).ConfigureAwait(false);
        return deleted > 0 ? StoreOutcome.Ok : StoreOutcome.NotFound;
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> SetAlertPausedAsync(int userId, int alertId, bool paused, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(Command(
            "UPDATE dbo.alert SET paused = @paused, armed = CASE WHEN @paused = 1 THEN armed ELSE NULL END WHERE user_id = @userId AND alert_id = @alertId",
            new { userId, alertId, paused },
            cancellationToken)).ConfigureAwait(false);
        return changed > 0 ? StoreOutcome.Ok : StoreOutcome.NotFound;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UserFire>> ReadFiresAsync(int userId, long? afterFireId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxFires);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = afterFireId is null
            ? "SELECT * FROM (SELECT TOP (@limit) f.fire_id AS FireId, f.alert_id AS AlertId, m.ticker AS Ticker, f.kind AS Kind, f.threshold AS Threshold, f.value AS Value, f.fired_at AS FiredAt FROM dbo.alert_fire f JOIN dbo.market m ON m.market_id = f.market_id WHERE f.user_id = @userId ORDER BY f.fire_id DESC) x ORDER BY FireId"
            : "SELECT TOP (@limit) f.fire_id AS FireId, f.alert_id AS AlertId, m.ticker AS Ticker, f.kind AS Kind, f.threshold AS Threshold, f.value AS Value, f.fired_at AS FiredAt FROM dbo.alert_fire f JOIN dbo.market m ON m.market_id = f.market_id WHERE f.user_id = @userId AND f.fire_id > @after ORDER BY f.fire_id";
        var rows = await connection.QueryAsync<FireRow>(Command(sql, new { userId, limit, after = afterFireId }, cancellationToken)).ConfigureAwait(false);
        return [.. rows
            .Select(r => AlertKinds.TryParse(r.Kind, out var kind) ? new UserFire(r.FireId, r.AlertId, r.Ticker, kind, r.Threshold, r.Value, Utc(r.FiredAt)) : null)
            .OfType<UserFire>()];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LayoutSummary>> ListLayoutsAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Name, DateTime UpdatedAt)>(Command("SELECT name AS Name, updated_at AS UpdatedAt FROM dbo.user_layout WHERE user_id = @userId ORDER BY name", new { userId }, cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new LayoutSummary(r.Name, Utc(r.UpdatedAt)))];
    }

    /// <inheritdoc/>
    public async Task<LayoutDetail?> GetLayoutAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await connection.QuerySingleOrDefaultAsync<(string Body, DateTime UpdatedAt)?>(Command("SELECT body AS Body, updated_at AS UpdatedAt FROM dbo.user_layout WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 64) }, cancellationToken)).ConfigureAwait(false);
        return found is { } layout ? new LayoutDetail(name, Utc(layout.UpdatedAt), layout.Body) : null;
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> PutLayoutAsync(int userId, string name, string body, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new { userId, name = Ansi(name, 64), body, now };
        const string update = "UPDATE dbo.user_layout SET body = @body, updated_at = @now WHERE user_id = @userId AND name = @name";
        if (await connection.ExecuteAsync(Command(update, parameters, cancellationToken)).ConfigureAwait(false) > 0)
        {
            return StoreOutcome.Ok;
        }

        if (await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM dbo.user_layout WHERE user_id = @userId", new { userId }, cancellationToken)).ConfigureAwait(false) >= MaxLayouts)
        {
            return StoreOutcome.LimitReached;
        }

        try
        {
            await connection.ExecuteAsync(Command("INSERT dbo.user_layout (user_id, name, body, updated_at) VALUES (@userId, @name, @body, @now)", parameters, cancellationToken)).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or UniqueConstraint)
        {
            // two saves of a new layout at the same moment: the other one made it, so this one replaces it
            await connection.ExecuteAsync(Command(update, parameters, cancellationToken)).ConfigureAwait(false);
        }

        return StoreOutcome.Ok;
    }

    /// <inheritdoc/>
    public async Task<StoreOutcome> DeleteLayoutAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync(Command("DELETE dbo.user_layout WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 64) }, cancellationToken)).ConfigureAwait(false);
        return deleted > 0 ? StoreOutcome.Ok : StoreOutcome.NotFound;
    }

    /// <inheritdoc/>
    public async Task<string?> GetSettingAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(Command("SELECT value FROM dbo.user_setting WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 32) }, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetSettingAsync(int userId, string name, string? value, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            await connection.ExecuteAsync(Command("DELETE dbo.user_setting WHERE user_id = @userId AND name = @name", new { userId, name = Ansi(name, 32) }, cancellationToken)).ConfigureAwait(false);
            return;
        }

        await connection.ExecuteAsync(Command(
            """
            UPDATE dbo.user_setting SET value = @value, updated_at = @now WHERE user_id = @userId AND name = @name;
            IF @@ROWCOUNT = 0 INSERT dbo.user_setting (user_id, name, value, updated_at) VALUES (@userId, @name, @value, @now);
            """,
            new { userId, name = Ansi(name, 32), value, now },
            cancellationToken)).ConfigureAwait(false);
    }

    private static CommandDefinition Command(string sql, object? parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, commandTimeout: 30, cancellationToken: cancellationToken);

    private static DbString Ansi(string value, int length) => new() { Value = value, IsAnsi = true, Length = length };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed record AlertRow(int AlertId, string Ticker, string Kind, long Threshold, int CooldownSeconds, bool Paused, DateTime CreatedAt, DateTime? LastEvaluatedAt, DateTime? LastFiredAt);

    private sealed record FireRow(long FireId, int AlertId, string Ticker, string Kind, long Threshold, long Value, DateTime FiredAt);
}
