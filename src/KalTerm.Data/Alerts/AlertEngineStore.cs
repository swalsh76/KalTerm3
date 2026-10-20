using Dapper;

namespace KalTerm.Data.Alerts;

/// <summary>An alert as the evaluator needs it.</summary>
/// <param name="AlertId">The alert.</param>
/// <param name="UserId">Whose it is.</param>
/// <param name="MarketId">The market it watches.</param>
/// <param name="Kind">What it watches for.</param>
/// <param name="Threshold">The threshold in the unit of the kind.</param>
/// <param name="CooldownSeconds">The least time between two fires.</param>
/// <param name="State">Its memory.</param>
public sealed record ActiveAlert(int AlertId, int UserId, int MarketId, AlertKind Kind, long Threshold, int CooldownSeconds, AlertState State);

/// <summary>What an evaluation did to one alert.</summary>
/// <param name="Alert">The alert.</param>
/// <param name="State">Its new memory.</param>
/// <param name="FireValue">The observed value when it fired; <see langword="null"/> when it did not.</param>
public sealed record AlertUpdate(ActiveAlert Alert, AlertState State, long? FireValue);

/// <summary>The alerts the evaluator works through and where it records what happened.</summary>
public interface IAlertEngineStore
{
    /// <summary>Every alert that is not paused.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The alerts.</returns>
    Task<IReadOnlyList<ActiveAlert>> ActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of an evaluation: each alert's memory and, for the ones that fired, the fire. All or nothing.</summary>
    /// <param name="updates">The outcomes.</param>
    /// <param name="now">The moment of the evaluation (UTC).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many fires were recorded.</returns>
    Task<int> ApplyAsync(IReadOnlyList<AlertUpdate> updates, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAlertEngineStore"/> over <c>dbo.alert</c> and <c>dbo.alert_fire</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class AlertEngineStore(DataSource source) : IAlertEngineStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ActiveAlert>> ActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT alert_id AS AlertId, user_id AS UserId, market_id AS MarketId, kind AS Kind, threshold AS Threshold, cooldown_s AS CooldownSeconds, armed AS Armed, last_fired_at AS LastFiredAt FROM dbo.alert WHERE paused = 0 ORDER BY alert_id",
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var alerts = new List<ActiveAlert>();
        foreach (var row in rows)
        {
            if (AlertKinds.TryParse(row.Kind, out var kind))
            {
                alerts.Add(new ActiveAlert(row.AlertId, row.UserId, row.MarketId, kind, row.Threshold, row.CooldownSeconds, new AlertState(row.Armed, row.LastFiredAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null)));
            }
        }

        return alerts;
    }

    /// <inheritdoc/>
    public async Task<int> ApplyAsync(IReadOnlyList<AlertUpdate> updates, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return 0;
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fires = 0;
        foreach (var update in updates)
        {
            var alert = update.Alert;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.alert SET armed = @armed, last_evaluated_at = @now, last_fired_at = @lastFiredAt WHERE alert_id = @alertId",
                new { alertId = alert.AlertId, armed = update.State.Armed, now, lastFiredAt = update.State.LastFiredAt },
                transaction,
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (update.FireValue is { } value)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT dbo.alert_fire (alert_id, user_id, market_id, kind, threshold, value, fired_at) VALUES (@alertId, @userId, @marketId, @kind, @threshold, @value, @now)",
                    new { alertId = alert.AlertId, userId = alert.UserId, marketId = alert.MarketId, kind = new DbString { Value = AlertKinds.Name(alert.Kind), IsAnsi = true, Length = 24 }, threshold = alert.Threshold, value, now },
                    transaction,
                    commandTimeout: 30,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                fires++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return fires;
    }

    private sealed record Row(int AlertId, int UserId, int MarketId, string Kind, long Threshold, int CooldownSeconds, bool? Armed, DateTime? LastFiredAt);
}
