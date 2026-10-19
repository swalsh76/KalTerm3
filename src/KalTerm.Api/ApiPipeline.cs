using KalTerm.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KalTerm.Api;

/// <summary>The middleware around the endpoints: security headers, one error shape for everything, and no detail about a failure leaving the process.</summary>
internal static partial class ApiPipeline
{
    public static void Use(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context).ConfigureAwait(false);
        });

        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("KalTerm.Api");
            if (failure is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                return;   // the client went away; nothing to say to it
            }

            // A database that cannot be reached or answered in time is the caller's retry, not a bug; anything else is ours. Neither says why.
            var unavailable = failure is Microsoft.Data.SqlClient.SqlException or TimeoutException;
            LogFailure(logger, failure!, unavailable);
            await Write(context, unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError, new ErrorDto(unavailable ? "unavailable" : "internal_error")).ConfigureAwait(false);
        }));

        // An empty 404 (no such route) or 405 (not a GET) gets the same error body as everything else.
        app.UseStatusCodePages(async context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            await Write(context.HttpContext, status, new ErrorDto(status switch { 404 => "not_found", 405 => "method_not_allowed", _ => "http_error" })).ConfigureAwait(false);
        });
    }

    private static async Task Write(HttpContext context, int status, ErrorDto error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(ApiJson.Serialize(error), context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "request failed (database unavailable: {Unavailable})")]
    private static partial void LogFailure(ILogger logger, Exception exception, bool unavailable);
}
