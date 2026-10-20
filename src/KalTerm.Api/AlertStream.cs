using System.Globalization;
using System.Text;
using KalTerm.Contracts;
using KalTerm.Data.Alerts;
using KalTerm.Data.Auth;
using KalTerm.Data.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api;

/// <summary>How many alert streams the server allows and how it keeps them honest.</summary>
public sealed record AlertStreamOptions
{
    /// <summary>The most streams one user can have open. Default 10.</summary>
    public int MaxPerUser { get; init; } = 10;

    /// <summary>The most streams the server can have open. Default 250.</summary>
    public int MaxTotal { get; init; } = 250;

    /// <summary>How often a quiet stream sends a keep-alive comment, so a dead connection is noticed. Default 15 seconds.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a stream checks that its token is still good. Default 30 seconds.</summary>
    public TimeSpan TokenCheck { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the counter that signals a fire is read. Default 1 second.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The most fires one read hands a stream, so a client far behind catches up in steps.</summary>
    public int Batch { get; init; } = 100;
}

/// <summary>
/// Keeps count of the open alert streams and tells them when an alert has fired. One watcher reads the fire counter; a stream asks <see cref="WaitForChangeAsync"/> and is woken only when the counter
/// has moved, so any number of idle streams cost one small read a second.
/// </summary>
public sealed partial class AlertStreamHub
{
    private readonly IAlertVersionReader _reader;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<int, int> _perUser = [];
    private TaskCompletionSource _changed = NewSignal();
    private int _total;
    private long _version;
    private bool _seeded;

    /// <summary>Creates the hub.</summary>
    /// <param name="reader">Reads the fire counter.</param>
    /// <param name="options">The limits.</param>
    /// <param name="logger">Receives the log.</param>
    public AlertStreamHub(IAlertVersionReader reader, AlertStreamOptions? options = null, ILogger<AlertStreamHub>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        Options = options ?? new AlertStreamOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStreamHub>.Instance;
    }

    /// <summary>The limits and timings.</summary>
    public AlertStreamOptions Options { get; }

    /// <summary>The fire counter as last read.</summary>
    public long Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    /// <summary>How many streams are open.</summary>
    public int Open
    {
        get
        {
            lock (_sync)
            {
                return _total;
            }
        }
    }

    /// <summary>Takes a place for a user's stream.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The place (dispose it when the stream ends), or <see langword="null"/> when the user or the server is at the limit.</returns>
    public IDisposable? TryAcquire(int userId)
    {
        lock (_sync)
        {
            if (_total >= Options.MaxTotal || _perUser.GetValueOrDefault(userId) >= Options.MaxPerUser)
            {
                return null;
            }

            _total++;
            _perUser[userId] = _perUser.GetValueOrDefault(userId) + 1;
        }

        return new Place(this, userId);
    }

    /// <summary>Reads the counter once; wakes the waiting streams if it moved. A failed read is logged and leaves the counter as it was.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the read is done.</returns>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        long read;
        try
        {
            read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailed(_logger, ex.Message);
            return;
        }

        TaskCompletionSource wake;
        lock (_sync)
        {
            if (_seeded && read == _version)
            {
                return;
            }

            _seeded = true;
            _version = read;
            wake = _changed;
            _changed = NewSignal();
        }

        wake.TrySetResult();
    }

    /// <summary>Waits until the counter is no longer <paramref name="seen"/>, up to <paramref name="timeout"/>.</summary>
    /// <param name="seen">The counter the caller last saw.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the counter changed, <see langword="false"/> on the timeout.</returns>
    public async Task<bool> WaitForChangeAsync(long seen, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (_sync)
        {
            if (_version != seen)
            {
                return true;
            }

            signal = _changed.Task;
        }

        try
        {
            await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Release(int userId)
    {
        lock (_sync)
        {
            _total--;
            if (_perUser.TryGetValue(userId, out var count))
            {
                if (count <= 1)
                {
                    _perUser.Remove(userId);
                }
                else
                {
                    _perUser[userId] = count - 1;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "could not read the alert counter: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string reason);

    private sealed class Place(AlertStreamHub hub, int userId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                hub.Release(userId);
            }
        }
    }
}

/// <summary>Runs <see cref="AlertStreamHub.PollAsync"/> for as long as the server runs.</summary>
/// <param name="hub">The hub.</param>
public sealed class AlertStreamWatcher(AlertStreamHub hub) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(hub.Options.Poll);
        try
        {
            do
            {
                await hub.PollAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}

/// <summary>The alert stream endpoint: Server-Sent Events of the caller's fires.</summary>
internal static class AlertStreamEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, IUserDataStore store, AlertStreamHub hub, TimeProvider time)
    {
        if (context.Items[ApiAuthentication.UserItem] is not AuthenticatedUser user)
        {
            return ApiEndpoints.Json(new ErrorDto("unauthorized"), ApiJson.Wire.ErrorDto, StatusCodes.Status401Unauthorized);
        }

        var parameters = new RequestParameters(context.Request.Query);
        var after = parameters.FireCursor();
        if (after is null && context.Request.Headers["Last-Event-ID"].Count == 1 && long.TryParse(context.Request.Headers["Last-Event-ID"][0], NumberStyles.None, CultureInfo.InvariantCulture, out var header))
        {
            after = header;
        }

        if (!parameters.IsValid)
        {
            return ApiEndpoints.Invalid([.. parameters.Invalid]);
        }

        var place = hub.TryAcquire(user.UserId);
        if (place is null)
        {
            context.Response.Headers.RetryAfter = "30";
            return ApiEndpoints.Json(new ErrorDto("too_many_streams"), ApiJson.Wire.ErrorDto, StatusCodes.Status429TooManyRequests);
        }

        using (place)
        {
            var aborted = context.RequestAborted;
            try
            {
                var last = after ?? (await store.ReadFiresAsync(user.UserId, null, 1, aborted).ConfigureAwait(false) is { Count: > 0 } newest ? newest[^1].FireId : 0);
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await context.Response.StartAsync(aborted).ConfigureAwait(false);
                await WriteAsync(context, string.Create(CultureInfo.InvariantCulture, $"id: {last}\nevent: ready\ndata: {{}}\n\n"), aborted).ConfigureAwait(false);

                var token = ApiAuthentication.BearerToken(context.Request);
                var nextTokenCheck = time.GetUtcNow() + hub.Options.TokenCheck;
                while (!aborted.IsCancellationRequested)
                {
                    var seen = hub.Version;   // before the read, so a fire between the read and the wait still wakes this stream
                    var fires = await store.ReadFiresAsync(user.UserId, last, hub.Options.Batch, aborted).ConfigureAwait(false);
                    foreach (var fire in fires)
                    {
                        var dto = new AlertFireDto(fire.FireId, fire.AlertId, fire.Ticker, AlertKinds.Name(fire.Kind), AlertText.Format(fire.Kind, fire.Threshold), AlertText.Format(fire.Kind, fire.Value), ApiFormat.Moment(fire.FiredAt));
                        await WriteAsync(context, string.Create(CultureInfo.InvariantCulture, $"id: {fire.FireId}\nevent: fire\ndata: {ApiJson.Serialize(dto)}\n\n"), aborted).ConfigureAwait(false);
                        last = fire.FireId;
                    }

                    if (fires.Count >= hub.Options.Batch)
                    {
                        continue;
                    }

                    if (!await hub.WaitForChangeAsync(seen, hub.Options.KeepAlive, aborted).ConfigureAwait(false))
                    {
                        await WriteAsync(context, ": keep-alive\n\n", aborted).ConfigureAwait(false);
                    }

                    if (token is not null && time.GetUtcNow() >= nextTokenCheck)
                    {
                        nextTokenCheck = time.GetUtcNow() + hub.Options.TokenCheck;
                        var current = await context.RequestServices.GetRequiredService<TokenAuthenticator>().AuthenticateAsync(token, aborted).ConfigureAwait(false);
                        if (current is null || current.UserId != user.UserId)
                        {
                            break;   // revoked, expired or disabled: the stream ends and the next connect is refused
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                // The client went away.
            }
        }

        return Results.Empty;
    }

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken cancellationToken)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
