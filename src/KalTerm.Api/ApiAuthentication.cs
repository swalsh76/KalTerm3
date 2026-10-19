using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using KalTerm.Contracts;
using KalTerm.Data.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api;

/// <summary>Whether requests need a token. Registered by <see cref="ApiEndpoints.AddKalTermApi"/>; switched off only by tests and a development run on loopback.</summary>
/// <param name="Required">True when a token is required.</param>
public sealed record ApiAuthOptions(bool Required);

/// <summary>Slows down someone guessing tokens: after too many failures from one address, that address is refused for a while, even with a good token.</summary>
public sealed class AuthThrottle
{
    /// <summary>How many failures from one address are tolerated in <see cref="Window"/>.</summary>
    public const int MaxFailures = 10;

    /// <summary>The time failures are counted over, and the time an address stays blocked after its last failure.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int MaxTracked = 10_000;
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new(StringComparer.Ordinal);

    /// <summary>Whether an address is currently refused.</summary>
    /// <param name="address">The address (any text that identifies the caller).</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>True if it has failed too often within the window.</returns>
    public bool IsBlocked(string address, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!_failures.TryGetValue(address, out var list))
        {
            return false;
        }

        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            return list.Count >= MaxFailures;
        }
    }

    /// <summary>Records a failed attempt.</summary>
    /// <param name="address">The caller.</param>
    /// <param name="now">The current UTC time.</param>
    public void RecordFailure(string address, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_failures.Count > MaxTracked)
        {
            _failures.Clear();   // a flood of distinct addresses must not grow memory without bound; forgetting everyone is the safe direction
        }

        var list = _failures.GetOrAdd(address, _ => []);
        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            list.Add(now);
        }
    }
}

/// <summary>Turns a token into a user, remembering the answer briefly so a busy client does not cost a database read per request.</summary>
public sealed class TokenAuthenticator
{
    /// <summary>How long a known token is remembered: also how long a revoked token can keep working.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private const int MaxCached = 1_000;
    private readonly IUserStore _users;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (AuthenticatedUser User, DateTime Until)> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the authenticator.</summary>
    /// <param name="users">Where tokens are looked up.</param>
    /// <param name="time">The clock.</param>
    public TokenAuthenticator(IUserStore users, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(time);
        _users = users;
        _time = time;
    }

    /// <summary>Finds the user a token belongs to.</summary>
    /// <param name="token">The token text.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The user, or <see langword="null"/> when the token is not valid now.</returns>
    public async Task<AuthenticatedUser?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!ApiTokens.HasShape(token))
        {
            return null;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var key = Convert.ToHexString(ApiTokens.Hash(token));
        if (_cache.TryGetValue(key, out var cached) && cached.Until > now)
        {
            return cached.User;
        }

        var user = await _users.AuthenticateAsync(token, now, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _cache.TryRemove(key, out _);
            return null;
        }

        if (_cache.Count > MaxCached)
        {
            _cache.Clear();
        }

        _cache[key] = (user, now + CacheFor);
        return user;
    }
}

/// <summary>
/// The sign-in check in front of the API. Everything needs <c>Authorization: Bearer &lt;token&gt;</c> except <c>/healthz</c> and <c>/readyz</c> (they say only that the process is alive and
/// whether the database answers) and, <b>on the plain-HTTP loopback listener only</b>, <c>/v1/status</c>, so checks run on the server itself need no token. A refusal is always the
/// same bare <c>401</c>: it never says whether the token was unknown, expired or revoked.
/// </summary>
internal static partial class ApiAuthentication
{
    public const string UserItem = "KalTerm.User";

    private static readonly PathString[] OpenEverywhere = ["/healthz", "/readyz"];

    public static void Use(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<ApiAuthOptions>();
            if (!options.Required || IsOpen(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            var throttle = context.RequestServices.GetRequiredService<AuthThrottle>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("KalTerm.Api.Auth");
            var now = time.GetUtcNow().UtcDateTime;
            var caller = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.IsBlocked(caller, now))
            {
                LogBlocked(logger, caller);
                context.Response.Headers.RetryAfter = ((int)AuthThrottle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await Write(context, StatusCodes.Status429TooManyRequests, new ErrorDto("too_many_attempts")).ConfigureAwait(false);
                return;
            }

            var user = BearerToken(context.Request) is { } token
                ? await context.RequestServices.GetRequiredService<TokenAuthenticator>().AuthenticateAsync(token, context.RequestAborted).ConfigureAwait(false)
                : null;
            if (user is null)
            {
                throttle.RecordFailure(caller, now);
                LogRefused(logger, caller);
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Write(context, StatusCodes.Status401Unauthorized, new ErrorDto("unauthorized")).ConfigureAwait(false);
                return;
            }

            context.Items[UserItem] = user;
            await next(context).ConfigureAwait(false);
        });
    }

    internal static bool IsOpen(HttpContext context)
    {
        var path = context.Request.Path;
        if (OpenEverywhere.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return path.Equals("/v1/status", StringComparison.OrdinalIgnoreCase)
            && !context.Request.IsHttps
            && context.Connection.RemoteIpAddress is { } remote
            && IPAddress.IsLoopback(remote)
            && context.Connection.LocalIpAddress is { } local
            && IPAddress.IsLoopback(local);
    }

    /// <summary>The token in an <c>Authorization: Bearer</c> header; <see langword="null"/> for anything else, including more than one header.</summary>
    internal static string? BearerToken(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { Length: > 7 and < 100 } header || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return header[7..].Trim();
    }

    private static async Task Write(HttpContext context, int status, ErrorDto error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(ApiJson.Serialize(error), context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refused a request without a valid token from {Caller}")]
    private static partial void LogRefused(ILogger logger, string caller);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refused a request from {Caller}: too many failed attempts")]
    private static partial void LogBlocked(ILogger logger, string caller);
}
