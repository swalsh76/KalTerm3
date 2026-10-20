using System.Globalization;
using KalTerm.Contracts;
using KalTerm.Data;
using KalTerm.Data.Auth;
using KalTerm.Data.Candles;
using KalTerm.Data.Reading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KalTerm.Api;

/// <summary>Whether the API can reach its database; behind <c>/readyz</c>.</summary>
public interface IReadinessProbe
{
    /// <summary>True when a trivial query succeeds.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether the database answered.</returns>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>The real probe: open a connection and ask for 1, giving up after a few seconds.</summary>
/// <param name="source">Opens connections.</param>
public sealed class DatabaseReadinessProbe(DataSource source) : IReadinessProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            await using var connection = await source.OpenAsync(limit.Token).ConfigureAwait(false);
            await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT 1", connection) { CommandTimeout = 3 };
            await command.ExecuteScalarAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;   // our own three-second limit
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return false;
        }
    }
}

/// <summary>The API: read-only <c>GET</c> endpoints over the readers. There is deliberately nothing here that writes, and nothing that talks to Kalshi.</summary>
public static class ApiEndpoints
{
    private const int MarketsMax = 500;
    private const int RowsMax = 1_000;
    private const int CandlesMax = 5_000;
    private const int DepthDefault = 10;
    private const int DepthMax = 100;

    /// <summary>Registers the real readers over a database.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="source">Opens connections.</param>
    /// <param name="options">Reader limits; <see langword="null"/> for the defaults.</param>
    /// <param name="requireToken">Whether requests need a token (always, except in tests and a development run on loopback).</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddKalTermApi(this IServiceCollection services, DataSource source, ReaderOptions? options = null, bool requireToken = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        services.AddSingleton<IMarketReader>(new MarketReader(source, options));
        services.AddSingleton<ITickReader>(new TickReader(source, options));
        services.AddSingleton<ITradeReader>(new TradeReader(source, options));
        services.AddSingleton<IGapReader>(new GapReader(source, options));
        services.AddSingleton<IBookReader>(new BookReader(source, options));
        services.AddSingleton<ICandleReader>(new CandleReader(source));
        services.AddSingleton<ICandleComputer>(new CandleBuilder(source));
        services.AddSingleton<ICandleService>(provider => new CandleService(
            provider.GetRequiredService<ICandleReader>(),
            provider.GetRequiredService<ICandleComputer>(),
            provider.GetRequiredService<IBookReader>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IStatusReader>(new StatusReader(source, options));
        services.AddSingleton<IReadinessProbe>(new DatabaseReadinessProbe(source));
        services.AddSingleton<IUserStore>(new UserStore(source));
        services.AddSingleton(new ApiAuthOptions(requireToken));
        services.AddSingleton<AuthThrottle>();
        services.AddSingleton(provider => new TokenAuthenticator(provider.GetRequiredService<IUserStore>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Adds the error, header and routing pipeline and maps the endpoints. The readers and a <see cref="TimeProvider"/> must be registered.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseKalTermApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ApiPipeline.Use(app);
        ApiAuthentication.Use(app);
        Map(app);
        return app;
    }

    private static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Json(new HealthDto("ok"), ApiJson.Wire.HealthDto));
        app.MapGet("/readyz", async (IReadinessProbe probe, CancellationToken cancellationToken) =>
            await probe.IsReadyAsync(cancellationToken).ConfigureAwait(false)
                ? Json(new HealthDto("ready"), ApiJson.Wire.HealthDto)
                : Json(new HealthDto("not_ready"), ApiJson.Wire.HealthDto, StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/v1/status", async (IStatusReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var snapshot = await reader.ReadAsync(now, StatusBuilder.GapWindow, cancellationToken).ConfigureAwait(false);
            return Json(StatusBuilder.Build(snapshot, now), ApiJson.Wire.StatusDto);
        });

        app.MapGet("/v1/markets", async (HttpRequest request, IMarketReader reader, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var limit = query.Limit(100, MarketsMax);
            var filter = new MarketFilter(query.Status(), query.Event(), query.Search());
            var after = query.MarketCursor();
            if (!query.IsValid)
            {
                return Invalid(query);
            }

            var page = await reader.ListAsync(filter, after, limit, cancellationToken).ConfigureAwait(false);
            return Json(new MarketPage([.. page.Rows.Select(Mapping.ToDto)], page.Next is null ? null : ApiFormat.EncodeCursor(page.Next.Ticker)), ApiJson.Wire.MarketPage);
        });

        app.MapGet("/v1/markets/{ticker}", async (string ticker, IMarketReader reader, CancellationToken cancellationToken) =>
        {
            if (!RequestParameters.IsTicker(ticker))
            {
                return Invalid("ticker");
            }

            return await reader.GetAsync(ticker, cancellationToken).ConfigureAwait(false) is { } market
                ? Json(Mapping.ToDto(market), ApiJson.Wire.MarketDto)
                : NotFound();
        });

        app.MapGet("/v1/markets/{ticker}/ticks", async (string ticker, HttpRequest request, ITickReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var limit = query.Limit(200, RowsMax);
            var (start, end) = Window(query, time);
            var after = query.TickCursor();
            if (!RequestParameters.IsTicker(ticker))
            {
                query.Reject("ticker");
            }

            if (!query.IsValid)
            {
                return Invalid(query);
            }

            var page = await reader.ReadAsync(ticker, start, end, after, limit, cancellationToken).ConfigureAwait(false);
            return page is null
                ? NotFound()
                : Json(new TickPage([.. page.Rows.Select(Mapping.ToDto)], page.Next is null ? null : ApiFormat.EncodeCursor(string.Create(CultureInfo.InvariantCulture, $"{ApiFormat.Moment(page.Next.Ts)}|{page.Next.Skip}"))), ApiJson.Wire.TickPage);
        });

        app.MapGet("/v1/markets/{ticker}/candles", async (string ticker, HttpRequest request, ICandleService candles, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var limit = query.Limit(200, CandlesMax);
            var interval = query.Interval();
            var (start, end) = Window(query, time);
            if (query.CandleCursor() is { } cursor)
            {
                start = cursor;
            }

            if (!RequestParameters.IsTicker(ticker))
            {
                query.Reject("ticker");
            }

            if (!query.IsValid)
            {
                return Invalid(query);
            }

            try
            {
                var result = await candles.GetAsync(new CandleQuery(ticker, interval, start, end, limit), cancellationToken).ConfigureAwait(false);
                return result is null
                    ? NotFound()
                    : Json(new CandlePage(CandleRules.IntervalText(interval), result.Source, [.. result.Candles.Select(Mapping.ToDto)], result.Next is null ? null : ApiFormat.EncodeCursor(ApiFormat.Moment(result.Next)!)), ApiJson.Wire.CandlePage);
            }
            catch (CandleRangeException)
            {
                return Invalid("start", "end", "interval");
            }
        });

        app.MapGet("/v1/markets/{ticker}/trades", async (string ticker, HttpRequest request, ITradeReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var limit = query.Limit(200, RowsMax);
            var (start, end) = Window(query, time);
            var after = query.TradeCursor();
            if (!RequestParameters.IsTicker(ticker))
            {
                query.Reject("ticker");
            }

            if (!query.IsValid)
            {
                return Invalid(query);
            }

            var page = await reader.ReadAsync(ticker, start, end, after, limit, cancellationToken).ConfigureAwait(false);
            return page is null
                ? NotFound()
                : Json(new TradePage([.. page.Rows.Select(Mapping.ToDto)], page.Next is null ? null : ApiFormat.EncodeCursor($"{ApiFormat.Moment(page.Next.Ts)}|{page.Next.TradeId}")), ApiJson.Wire.TradePage);
        });

        app.MapGet("/v1/orderbooks", async (HttpRequest request, IBookReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var depth = query.Depth(DepthDefault, DepthMax);
            if (!query.IsValid)
            {
                return Invalid(query);
            }

            var now = time.GetUtcNow().UtcDateTime;
            var books = await reader.ListAsync(cancellationToken).ConfigureAwait(false);
            return Json(new OrderBookPage([.. books.Select(b => Mapping.ToDto(b, now, depth))]), ApiJson.Wire.OrderBookPage);
        });

        app.MapGet("/v1/markets/{ticker}/orderbook", async (string ticker, HttpRequest request, IBookReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var depth = query.Depth(DepthDefault, DepthMax);
            if (!RequestParameters.IsTicker(ticker))
            {
                query.Reject("ticker");
            }

            if (!query.IsValid)
            {
                return Invalid(query);
            }

            return await reader.GetAsync(ticker, cancellationToken).ConfigureAwait(false) is { } book
                ? Json(Mapping.ToDto(book, time.GetUtcNow().UtcDateTime, depth), ApiJson.Wire.OrderBookDto)
                : NotFound();
        });

        app.MapGet("/v1/gaps", async (HttpRequest request, IGapReader reader, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var query = new RequestParameters(request.Query);
            var limit = query.Limit(100, RowsMax);
            var now = time.GetUtcNow().UtcDateTime;
            var start = query.Moment("start");
            var end = RequestParameters.EndOrNow(query.Moment("end"), now);
            if (start is { } given && given >= end)
            {
                query.Reject("start", "end");
            }

            if (!query.IsValid)
            {
                return Invalid(query);
            }

            var gaps = await reader.ReadAsync(start ?? now.AddDays(-7), end, limit, cancellationToken).ConfigureAwait(false);
            return Json(new GapPage([.. gaps.Select(Mapping.ToDto)]), ApiJson.Wire.GapPage);
        });
    }

    /// <summary>The window of a ticks or trades request: an optional start, and an end that defaults to just after now. A start at or after the end is refused.</summary>
    private static (DateTime? Start, DateTime End) Window(RequestParameters query, TimeProvider time)
    {
        var start = query.Moment("start");
        var end = RequestParameters.EndOrNow(query.Moment("end"), time.GetUtcNow().UtcDateTime);
        if (start is { } given && given >= end)
        {
            query.Reject("start", "end");
        }

        return (start, end);
    }

    private static IResult Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, int status = StatusCodes.Status200OK) =>
        Results.Json(value, info, statusCode: status);

    private static IResult Invalid(RequestParameters query) => Invalid([.. query.Invalid]);

    private static IResult Invalid(params string[] fields) =>
        Json(new ErrorDto("invalid_parameter", fields), ApiJson.Wire.ErrorDto, StatusCodes.Status422UnprocessableEntity);

    private static IResult NotFound() => Json(new ErrorDto("not_found"), ApiJson.Wire.ErrorDto, StatusCodes.Status404NotFound);
}
