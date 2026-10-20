using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KalTerm.Contracts;
using KalTerm.Data.Alerts;
using KalTerm.Data.Auth;
using KalTerm.Data.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KalTerm.Api;

/// <summary>How alert thresholds and values are written and read: dollars for prices and spreads, a whole factor for a volume spike, contracts for open interest.</summary>
public static partial class AlertText
{
    /// <summary>Writes a threshold or a value in the unit of its kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="stored">The stored number.</param>
    /// <returns>The text.</returns>
    public static string Format(AlertKind kind, long stored) => kind switch
    {
        AlertKind.VolumeSpike => stored.ToString(CultureInfo.InvariantCulture),
        AlertKind.OiChange => ApiFormat.Count(stored)!,
        _ => ApiFormat.Dollars(stored)!,
    };

    /// <summary>Reads a threshold: plain digits with an optional decimal point and no sign or exponent, at most 6 decimals for dollars, 2 for contracts and none for a factor.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="text">The text.</param>
    /// <param name="stored">The stored number.</param>
    /// <returns>Whether the text is a usable threshold for the kind.</returns>
    public static bool TryParse(AlertKind kind, string? text, out long stored)
    {
        stored = 0;
        if (text is null || !Shape().IsMatch(text))
        {
            return false;
        }

        var (scale, decimals) = kind switch
        {
            AlertKind.VolumeSpike => (1m, 0),
            AlertKind.OiChange => (100m, 2),
            _ => (1_000_000m, 6),
        };
        var point = text.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0 && text.Length - point - 1 > decimals)
        {
            return false;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var scaled = value * scale;
        if (scaled > long.MaxValue || scaled != decimal.Truncate(scaled))
        {
            return false;
        }

        stored = (long)scaled;
        return AlertKinds.IsValidThreshold(kind, stored);
    }

    [GeneratedRegex("^[0-9]{1,12}(\\.[0-9]{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>
/// The endpoints about the caller's own data: watchlists, alerts, the alerts that fired and settings. The user is whoever the token belongs to; there is no way to name another. Nothing here is
/// reachable without a user, so a server that does not require tokens answers <c>401</c> to all of it.
/// </summary>
internal static partial class UserEndpoints
{
    private const int MaxBodyBytes = 4_096;
    private const int FiresMax = 1_000;
    private const string TimeZoneSetting = "time_zone";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/watchlists", async (HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
            User(context) is not { } user
                ? Unauthorized()
                : ApiEndpoints.Json(new WatchlistListDto([.. (await store.ListWatchlistsAsync(user.UserId, cancellationToken).ConfigureAwait(false)).Select(w => new WatchlistSummaryDto(w.Name, w.Markets, ApiFormat.Moment(w.CreatedAt)))]), ApiJson.Wire.WatchlistListDto));

        app.MapPost("/v1/watchlists", async (HttpContext context, IUserDataStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            var body = await ReadAsync(context.Request, ApiJson.Wire.CreateWatchlistRequest, cancellationToken).ConfigureAwait(false);
            if (body is null || !WatchlistName().IsMatch(body.Name ?? string.Empty))
            {
                return ApiEndpoints.Invalid("name");
            }

            var now = time.GetUtcNow().UtcDateTime;
            return await store.CreateWatchlistAsync(user.UserId, body.Name!, now, cancellationToken).ConfigureAwait(false) switch
            {
                StoreOutcome.Ok => ApiEndpoints.Json(new WatchlistDto(body.Name!, ApiFormat.Moment(now), []), ApiJson.Wire.WatchlistDto, StatusCodes.Status201Created),
                StoreOutcome.Exists => Conflict("already_exists"),
                _ => Conflict("limit_reached"),
            };
        });

        app.MapGet("/v1/watchlists/{name}", async (string name, HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            if (!WatchlistName().IsMatch(name))
            {
                return ApiEndpoints.Invalid("name");
            }

            return await store.GetWatchlistAsync(user.UserId, name, cancellationToken).ConfigureAwait(false) is { } list
                ? ApiEndpoints.Json(new WatchlistDto(list.Name, ApiFormat.Moment(list.CreatedAt), list.Tickers), ApiJson.Wire.WatchlistDto)
                : ApiEndpoints.NotFound();
        });

        app.MapDelete("/v1/watchlists/{name}", async (string name, HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            if (!WatchlistName().IsMatch(name))
            {
                return ApiEndpoints.Invalid("name");
            }

            return await store.DeleteWatchlistAsync(user.UserId, name, cancellationToken).ConfigureAwait(false) == StoreOutcome.Ok ? Results.NoContent() : ApiEndpoints.NotFound();
        });

        app.MapPut("/v1/watchlists/{name}/markets/{ticker}", async (string name, string ticker, HttpContext context, IUserDataStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            var bad = new List<string>();
            if (!WatchlistName().IsMatch(name))
            {
                bad.Add("name");
            }

            if (!RequestParameters.IsTicker(ticker))
            {
                bad.Add("ticker");
            }

            if (bad.Count > 0)
            {
                return ApiEndpoints.Invalid([.. bad]);
            }

            return await store.AddMarketAsync(user.UserId, name, ticker, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false) switch
            {
                StoreOutcome.Ok => Results.NoContent(),
                StoreOutcome.NotFound => ApiEndpoints.NotFound(),
                StoreOutcome.MarketNotFound => ApiEndpoints.Json(new ErrorDto("market_not_found"), ApiJson.Wire.ErrorDto, StatusCodes.Status404NotFound),
                _ => Conflict("limit_reached"),
            };
        });

        app.MapDelete("/v1/watchlists/{name}/markets/{ticker}", async (string name, string ticker, HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            if (!WatchlistName().IsMatch(name) || !RequestParameters.IsTicker(ticker))
            {
                return ApiEndpoints.Invalid(!WatchlistName().IsMatch(name) ? "name" : "ticker");
            }

            return await store.RemoveMarketAsync(user.UserId, name, ticker, cancellationToken).ConfigureAwait(false) == StoreOutcome.Ok ? Results.NoContent() : ApiEndpoints.NotFound();
        });

        app.MapGet("/v1/alerts", async (HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
            User(context) is not { } user
                ? Unauthorized()
                : ApiEndpoints.Json(new AlertListDto([.. (await store.ListAlertsAsync(user.UserId, cancellationToken).ConfigureAwait(false)).Select(ToDto)]), ApiJson.Wire.AlertListDto));

        app.MapPost("/v1/alerts", async (HttpContext context, IUserDataStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            var body = await ReadAsync(context.Request, ApiJson.Wire.CreateAlertRequest, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return ApiEndpoints.Invalid("body");
            }

            var bad = new List<string>();
            if (!RequestParameters.IsTicker(body.Ticker ?? string.Empty))
            {
                bad.Add("ticker");
            }

            var kindKnown = AlertKinds.TryParse(body.Kind, out var kind);
            if (!kindKnown)
            {
                bad.Add("kind");
            }

            long threshold = 0;
            if (kindKnown && !AlertText.TryParse(kind, body.Threshold, out threshold))
            {
                bad.Add("threshold");
            }

            var cooldown = body.CooldownSeconds ?? AlertKinds.DefaultCooldownSeconds;
            if (cooldown is < AlertKinds.MinCooldownSeconds or > AlertKinds.MaxCooldownSeconds)
            {
                bad.Add("cooldown_seconds");
            }

            if (bad.Count > 0)
            {
                return ApiEndpoints.Invalid([.. bad]);
            }

            var (outcome, id) = await store.AddAlertAsync(user.UserId, body.Ticker!, kind, threshold, cooldown, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            if (outcome == StoreOutcome.MarketNotFound)
            {
                return ApiEndpoints.Json(new ErrorDto("market_not_found"), ApiJson.Wire.ErrorDto, StatusCodes.Status404NotFound);
            }

            if (outcome != StoreOutcome.Ok)
            {
                return Conflict("limit_reached");
            }

            var created = (await store.ListAlertsAsync(user.UserId, cancellationToken).ConfigureAwait(false)).First(a => a.AlertId == id);
            return ApiEndpoints.Json(ToDto(created), ApiJson.Wire.AlertDto, StatusCodes.Status201Created);
        });

        app.MapMethods("/v1/alerts/{id}", ["PATCH"], async (string id, HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var alertId))
            {
                return ApiEndpoints.Invalid("id");
            }

            var body = await ReadAsync(context.Request, ApiJson.Wire.PatchAlertRequest, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return ApiEndpoints.Invalid("body");
            }

            return await store.SetAlertPausedAsync(user.UserId, alertId, body.Paused, cancellationToken).ConfigureAwait(false) == StoreOutcome.Ok ? Results.NoContent() : ApiEndpoints.NotFound();
        });

        app.MapDelete("/v1/alerts/{id}", async (string id, HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var alertId))
            {
                return ApiEndpoints.Invalid("id");
            }

            return await store.DeleteAlertAsync(user.UserId, alertId, cancellationToken).ConfigureAwait(false) == StoreOutcome.Ok ? Results.NoContent() : ApiEndpoints.NotFound();
        });

        app.MapGet("/v1/alerts/stream", AlertStreamEndpoint.HandleAsync);

        app.MapGet("/v1/alerts/fires", async (HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            var query = new RequestParameters(context.Request.Query);
            var limit = query.Limit(50, FiresMax);
            var after = query.FireCursor();
            if (!query.IsValid)
            {
                return ApiEndpoints.Invalid([.. query.Invalid]);
            }

            var fires = await store.ReadFiresAsync(user.UserId, after, limit, cancellationToken).ConfigureAwait(false);
            return ApiEndpoints.Json(
                new AlertFiresPage([.. fires.Select(f => new AlertFireDto(f.FireId, f.AlertId, f.Ticker, AlertKinds.Name(f.Kind), AlertText.Format(f.Kind, f.Threshold), AlertText.Format(f.Kind, f.Value), ApiFormat.Moment(f.FiredAt)))], fires.Count == 0 ? null : fires[^1].FireId),
                ApiJson.Wire.AlertFiresPage);
        });

        app.MapGet("/v1/me/settings/time_zone", async (HttpContext context, IUserDataStore store, CancellationToken cancellationToken) =>
            User(context) is not { } user
                ? Unauthorized()
                : ApiEndpoints.Json(new SettingDto(TimeZoneSetting, await store.GetSettingAsync(user.UserId, TimeZoneSetting, cancellationToken).ConfigureAwait(false)), ApiJson.Wire.SettingDto));

        app.MapPut("/v1/me/settings/time_zone", async (HttpContext context, IUserDataStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            var body = await ReadAsync(context.Request, ApiJson.Wire.SettingValueRequest, cancellationToken).ConfigureAwait(false);
            if (body?.Value is not { } zone || !KnownZone(zone))
            {
                return ApiEndpoints.Invalid("value");
            }

            await store.SetSettingAsync(user.UserId, TimeZoneSetting, zone, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            return ApiEndpoints.Json(new SettingDto(TimeZoneSetting, zone), ApiJson.Wire.SettingDto);
        });

        app.MapDelete("/v1/me/settings/time_zone", async (HttpContext context, IUserDataStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (User(context) is not { } user)
            {
                return Unauthorized();
            }

            await store.SetSettingAsync(user.UserId, TimeZoneSetting, null, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    /// <summary>Whether this computer knows a time zone by that name (checked here so a bad name is never stored).</summary>
    internal static bool KnownZone(string name)
    {
        if (name.Length is < 1 or > 64 || !ZoneName().IsMatch(name))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(name);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static AuthenticatedUser? User(HttpContext context) => context.Items[ApiAuthentication.UserItem] as AuthenticatedUser;

    private static IResult Unauthorized() => ApiEndpoints.Json(new ErrorDto("unauthorized"), ApiJson.Wire.ErrorDto, StatusCodes.Status401Unauthorized);

    private static IResult Conflict(string code) => ApiEndpoints.Json(new ErrorDto(code), ApiJson.Wire.ErrorDto, StatusCodes.Status409Conflict);

    private static AlertDto ToDto(UserAlert a) => new(
        a.AlertId,
        a.Ticker,
        AlertKinds.Name(a.Kind),
        AlertText.Format(a.Kind, a.Threshold),
        a.CooldownSeconds,
        a.Paused,
        ApiFormat.Moment(a.CreatedAt),
        ApiFormat.Moment(a.LastEvaluatedAt),
        ApiFormat.Moment(a.LastFiredAt));

    /// <summary>The JSON body of a request, or <see langword="null"/> when it is missing, too long or not the expected JSON. Never throws on a bad body.</summary>
    private static async Task<T?> ReadAsync<T>(HttpRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
        where T : class
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1_024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(), info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex WatchlistName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_/+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneName();
}
