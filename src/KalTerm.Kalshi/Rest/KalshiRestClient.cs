using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Core;
using KalTerm.Core.Clock;
using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;
using KalTerm.Kalshi.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Kalshi.Rest;

/// <summary>
/// An asynchronous, read-only client for the Kalshi trade API. Read-only is structural: there are no write methods, and a
/// transport-level guard rejects any request that is not a GET before it is sent. Every attempt waits for rate-limit
/// tokens and, if a signer is configured, is signed afresh. Responses are accepted only on HTTP 200; 429 and 5xx answers and
/// transport errors are retried with exponential backoff and jitter, anything else is a <see cref="KalshiApiException"/>.
/// </summary>
public sealed partial class KalshiRestClient : IDisposable
{
    private const double BackoffBaseSeconds = 0.5;
    private const double BackoffMaxSeconds = 8.0;

    private readonly HttpClient _http;
    private readonly TokenBucket _bucket;
    private readonly bool _ownsBucket;
    private readonly KalshiSigner? _signer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<double> _jitter;
    private readonly int _maxRetries;
    private readonly string _baseUrl;
    private readonly string _basePath;

    /// <param name="settings">Environment (demo or production) and rate budgets.</param>
    /// <param name="options">Signer, bucket, clock, logger and retry settings; defaults apply when omitted.</param>
    /// <param name="handler">
    /// The HTTP handler; a standard one when omitted. The client takes ownership of it and disposes it.
    /// </param>
    public KalshiRestClient(KalshiSettings settings, KalshiRestClientOptions? options = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new KalshiRestClientOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);

        _time = options.TimeProvider ?? TimeProvider.System;
        _logger = options.Logger ?? NullLogger.Instance;
        _signer = options.Signer;
        _maxRetries = options.MaxRetries;
        _jitter = options.JitterSample ?? Random.Shared.NextDouble;
        _baseUrl = settings.RestUrl.AbsoluteUri.TrimEnd('/');
        _basePath = settings.RestUrl.AbsolutePath.TrimEnd('/');

        if (options.Bucket is null)
        {
            _bucket = KalshiRateLimits.ReadBucket(settings, _time);
            _ownsBucket = true;
        }
        else
        {
            _bucket = options.Bucket;
        }

        _http = new HttpClient(new ReadOnlyGuardHandler(handler ?? new SocketsHttpHandler()), disposeHandler: true)
        {
            Timeout = options.Timeout,
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KalTerm", BuildInfo.Version.Split('+')[0]));

        if (settings.IsProduction)
        {
            LogProduction(_logger);
        }
    }

    /// <summary>The underlying client, behind the read-only guard; for tests.</summary>
    internal HttpClient Http => _http;

    /// <summary>Whether the exchange is open and trading.</summary>
    public async Task<ExchangeStatus> ExchangeStatusAsync(CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync("/exchange/status", null, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.ExchangeStatus,
            "exchange status");

    /// <summary>
    /// One page of markets. Unfiltered results include multivariate (combo) markets; filter with <c>mve_filter</c>
    /// set to <c>exclude</c> or <c>only</c> to split them.
    /// </summary>
    public async Task<MarketsPage> MarketsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync("/markets", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.MarketsPage,
            "markets");

    /// <summary>Every market matching the query, following Kalshi's cursor pagination until it returns an empty cursor.</summary>
    public IAsyncEnumerable<Market> IterateMarketsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        IterateAsync("/markets", query, KalshiJsonContext.Default.MarketsPage, page => page.Markets, page => page.Cursor, cancellationToken);

    /// <summary>The page size discovery asks for; Kalshi's maximum for markets.</summary>
    public const int MarketPageSize = 1000;

    /// <summary>
    /// Every open, non-combo market: <c>status=open</c>, <c>mve_filter=exclude</c>, 1,000 per page. This is the set the ingest watches
    /// (PLAN.md decision D8).
    /// </summary>
    public IAsyncEnumerable<Market> IterateOpenMarketsAsync(CancellationToken cancellationToken = default) =>
        IterateMarketsAsync(KalshiQuery.Empty.With("status", "open").With("mve_filter", "exclude").With("limit", MarketPageSize), cancellationToken);

    /// <summary>
    /// Every non-combo market whose metadata changed at or after <paramref name="since"/>: new markets, status changes, closes and
    /// settlements. <c>min_updated_ts</c> is valid together with <c>mve_filter=exclude</c> but not with <c>status</c>, so markets come back in
    /// whatever status they now have, which is how a market leaving the open list is seen with its true status. Prices and volume do not count as changes.
    /// </summary>
    /// <param name="since">The moment to look back to; sent as whole epoch seconds.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The changed markets.</returns>
    public IAsyncEnumerable<Market> IterateMarketsUpdatedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
        IterateMarketsAsync(KalshiQuery.Empty.With("min_updated_ts", since).With("mve_filter", "exclude").With("limit", MarketPageSize), cancellationToken);

    /// <summary>
    /// Executed trades across all markets, combos included. Filter with <c>min_ts</c> / <c>max_ts</c> (epoch seconds) and
    /// optionally <c>ticker</c>; used to backfill what a disconnect missed.
    /// </summary>
    public IAsyncEnumerable<Trade> IterateTradesAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        IterateAsync("/markets/trades", query, KalshiJsonContext.Default.TradesPage, page => page.Trades, page => page.Cursor, cancellationToken);

    /// <summary>One page of ordinary events (Kalshi excludes multivariate events here).</summary>
    public async Task<EventsPage> EventsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync("/events", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.EventsPage,
            "events");

    /// <summary>Every ordinary event matching the query.</summary>
    public IAsyncEnumerable<KalshiEvent> IterateEventsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        IterateAsync("/events", query, KalshiJsonContext.Default.EventsPage, page => page.Events, page => page.Cursor, cancellationToken);

    /// <summary>One page of multivariate (combo) events; filter by <c>series_ticker</c> or <c>collection_ticker</c> (not both).</summary>
    public async Task<EventsPage> MultivariateEventsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync("/events/multivariate", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.EventsPage,
            "multivariate events");

    /// <summary>Every multivariate (combo) event matching the query. Never touches the ordinary events endpoint.</summary>
    public IAsyncEnumerable<KalshiEvent> IterateMultivariateEventsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        IterateAsync("/events/multivariate", query, KalshiJsonContext.Default.EventsPage, page => page.Events, page => page.Cursor, cancellationToken);

    /// <summary>One market by ticker.</summary>
    public async Task<Market> MarketAsync(string ticker, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync($"/markets/{Segment(ticker)}", null, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.MarketEnvelope,
            $"market {ticker}").Market;

    /// <summary>One event by ticker.</summary>
    public async Task<KalshiEvent> EventAsync(string eventTicker, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync($"/events/{Segment(eventTicker)}", null, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.EventEnvelope,
            $"event {eventTicker}").Event;

    /// <summary>One series by ticker.</summary>
    public async Task<Series> SeriesAsync(string seriesTicker, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync($"/series/{Segment(seriesTicker)}", null, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.SeriesEnvelope,
            $"series {seriesTicker}").Series;

    /// <summary>The list of series, optionally filtered.</summary>
    public async Task<IReadOnlyList<Series>> SeriesListAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default) =>
        KalshiJson.Parse(
            await GetStringAsync("/series", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.SeriesList,
            "series list").Series;

    /// <summary>A market's order book. <paramref name="depth"/> limits the levels per side; 0 asks for all of them.</summary>
    public async Task<OrderBook> OrderBookAsync(string ticker, int depth = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);

        return OrderBook.FromResponse(
            await GetStringAsync($"/markets/{Segment(ticker)}/orderbook", KalshiQuery.Empty.With("depth", depth), cancellationToken).ConfigureAwait(false));
    }

    // --- account (portfolio) reads ---------------------------------------------------------------------------------------------------
    // These need a signed client and a key with read scope. They are plain GETs through the same guarded path as everything else: this client still cannot send anything else.

    /// <summary>The account balance, per exchange shard unless <paramref name="exchangeIndex"/> narrows it. Use the dollar fields: the cents field is truncated.</summary>
    /// <param name="exchangeIndex">Only this shard.</param>
    /// <param name="subaccount">A subaccount (0 is the primary).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The balance.</returns>
    public async Task<AccountBalance> BalanceAsync(int? exchangeIndex = null, int? subaccount = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/balance");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/balance", KalshiQuery.Empty.With("exchange_index", exchangeIndex).With("subaccount", subaccount), cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.AccountBalance,
            "balance");
    }

    /// <summary>One page of positions (market and event). Filters: <c>ticker</c>, <c>event_ticker</c>, <c>settlement_status</c>, <c>count_filter</c>, <c>exchange_index</c>, <c>limit</c>, <c>cursor</c>.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public async Task<PositionsPage> PositionsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/positions");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/positions", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.PositionsPage,
            "positions");
    }

    /// <summary>Every market position matching the query, following the cursor.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The positions.</returns>
    public IAsyncEnumerable<MarketPosition> IteratePositionsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/positions");
        return IterateAsync("/portfolio/positions", query, KalshiJsonContext.Default.PositionsPage, page => page.MarketPositions ?? [], page => page.Cursor, cancellationToken);
    }

    /// <summary>One page of orders. Filters: <c>ticker</c>, <c>status</c>, <c>min_ts</c>, <c>max_ts</c>, <c>limit</c>, <c>cursor</c>.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public async Task<OrdersPage> OrdersPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/orders");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/orders", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.OrdersPage,
            "orders");
    }

    /// <summary>Every order matching the query, following the cursor.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The orders.</returns>
    public IAsyncEnumerable<Order> IterateOrdersAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/orders");
        return IterateAsync("/portfolio/orders", query, KalshiJsonContext.Default.OrdersPage, page => page.Orders ?? [], page => page.Cursor, cancellationToken);
    }

    /// <summary>One order by its id.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The order.</returns>
    public async Task<Order> OrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/orders/{id}");
        return KalshiJson.Parse(
            await GetStringAsync($"/portfolio/orders/{Segment(orderId)}", null, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.OrderEnvelope,
            $"order {orderId}").Order;
    }

    /// <summary>One page of fills. Filters: <c>ticker</c>, <c>order_id</c>, <c>min_ts</c>, <c>max_ts</c>, <c>limit</c>, <c>cursor</c>.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public async Task<FillsPage> FillsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/fills");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/fills", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.FillsPage,
            "fills");
    }

    /// <summary>Every fill matching the query, following the cursor.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The fills.</returns>
    public IAsyncEnumerable<Fill> IterateFillsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/fills");
        return IterateAsync("/portfolio/fills", query, KalshiJsonContext.Default.FillsPage, page => page.Fills ?? [], page => page.Cursor, cancellationToken);
    }

    /// <summary>One page of settlements. Filters: <c>ticker</c>, <c>event_ticker</c>, <c>min_ts</c>, <c>max_ts</c>, <c>limit</c>, <c>cursor</c>.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public async Task<SettlementsPage> SettlementsPageAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/settlements");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/settlements", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.SettlementsPage,
            "settlements");
    }

    /// <summary>Every settlement matching the query, following the cursor.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The settlements.</returns>
    public IAsyncEnumerable<Settlement> IterateSettlementsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/settlements");
        return IterateAsync("/portfolio/settlements", query, KalshiJsonContext.Default.SettlementsPage, page => page.Settlements ?? [], page => page.Cursor, cancellationToken);
    }

    /// <summary>Where resting orders stand in the queue. Filters: <c>market_tickers</c> (comma-separated) or <c>event_ticker</c>.</summary>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The queue positions.</returns>
    public async Task<IReadOnlyList<QueuePosition>> QueuePositionsAsync(KalshiQuery? query = null, CancellationToken cancellationToken = default)
    {
        RequireSigner("/portfolio/orders/queue_positions");
        return KalshiJson.Parse(
            await GetStringAsync("/portfolio/orders/queue_positions", query, cancellationToken).ConfigureAwait(false),
            KalshiJsonContext.Default.QueuePositionsReply,
            "queue positions").Positions ?? [];
    }

    /// <summary>An account endpoint without a signature would only be refused; saying so here names the mistake and spends no request.</summary>
    private void RequireSigner(string path)
    {
        if (_signer is null)
        {
            throw new AuthException($"{path} is an account endpoint: the client needs a signer (a key) to read it");
        }
    }

    /// <summary>
    /// Estimates how far this machine's clock is from Kalshi's. Signed requests embed a timestamp, so a badly skewed clock makes every
    /// request fail. Kalshi publishes no time endpoint, so this takes <paramref name="samples"/> readings of the HTTP <c>Date</c>
    /// header from <c>/exchange/status</c>, spaced by <paramref name="spacing"/> (between samples only), and combines them with
    /// <see cref="ClockSkewEstimator"/>. Each sample is timed around the attempt that succeeded, not around any rate-limit wait
    /// or retry, which keeps the interval as tight as it can be.
    /// </summary>
    public async Task<ClockSkew> ClockSkewAsync(
        int samples = 5,
        TimeSpan? spacing = null,
        TimeSpan? threshold = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        var gap = spacing ?? TimeSpan.FromMilliseconds(250);
        ArgumentOutOfRangeException.ThrowIfLessThan(gap, TimeSpan.Zero, nameof(spacing));

        var taken = new List<ClockSample>(samples);
        for (var i = 0; i < samples; i++)
        {
            if (i > 0)
            {
                await Task.Delay(gap, _time, cancellationToken).ConfigureAwait(false);
            }

            var response = await GetResponseAsync("/exchange/status", null, cancellationToken).ConfigureAwait(false);
            taken.Add(ClockSample.FromHeaders(response.Sent, response.Received, response.DateHeader, response.AgeHeader));
        }

        return ClockSkewEstimator.Estimate(taken, threshold);
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsBucket)
        {
            _bucket.Dispose();
        }
    }

    /// <summary>The body of a 200 response, which is all most endpoints need.</summary>
    internal async Task<string> GetStringAsync(string path, KalshiQuery? query, CancellationToken cancellationToken) =>
        (await GetResponseAsync(path, query, cancellationToken).ConfigureAwait(false)).Body;

    /// <summary>
    /// Sends a GET and returns the 200 response with its timing and the headers the clock check needs. This is the single place
    /// requests leave the client: it paces, signs, sends and retries.
    /// </summary>
    internal async Task<KalshiResponse> GetResponseAsync(string path, KalshiQuery? query, CancellationToken cancellationToken)
    {
        var url = new Uri(_baseUrl + path + (query ?? KalshiQuery.Empty).ToQueryString(), UriKind.Absolute);

        for (var attempt = 0; ; attempt++)
        {
            await _bucket.AcquireAsync(cancellationToken: cancellationToken).ConfigureAwait(false);   // every attempt, retries included

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_signer is not null)
            {
                foreach (var header in _signer.Headers("GET", _basePath + path))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);   // signed over the full path, without the query
                }
            }

            var isLast = attempt == _maxRetries;
            string reason;
            try
            {
                var sent = _time.GetUtcNow();
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var received = _time.GetUtcNow();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return new KalshiResponse(body, sent, received, RawHeader(response, "Date"), RawHeader(response, "Age"));
                }

                if (!IsRetryable(response.StatusCode) || isLast)
                {
                    throw new KalshiApiException((int)response.StatusCode, body);
                }

                reason = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (!isLast && ex is HttpRequestException or IOException)
            {
                reason = ex.GetType().Name;   // a connection that fails before or while the body is read
            }
            catch (TaskCanceledException) when (!isLast && !cancellationToken.IsCancellationRequested)
            {
                reason = "timeout";   // the HTTP timeout, not the caller cancelling
            }

            var delay = Backoff(attempt);
            LogRetry(_logger, path, attempt + 1, reason, delay.TotalSeconds);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Segment(string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        return Uri.EscapeDataString(ticker);
    }

    /// <summary>A header exactly as the server sent it (the typed accessors would re-format a date).</summary>
    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>min(8 s, 0.5 s x 2^attempt), scaled by a jitter factor in [0.5, 1.0).</summary>
    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(BackoffMaxSeconds, BackoffBaseSeconds * Math.Pow(2.0, attempt));
        return TimeSpan.FromSeconds(seconds * (0.5 + (0.5 * _jitter())));
    }

    private async IAsyncEnumerable<TItem> IterateAsync<TPage, TItem>(
        string path,
        KalshiQuery? query,
        JsonTypeInfo<TPage> pageInfo,
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, string> cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TPage : class
    {
        string? next = null;
        while (true)
        {
            var body = await GetStringAsync(path, (query ?? KalshiQuery.Empty).With("cursor", next), cancellationToken).ConfigureAwait(false);
            var page = KalshiJson.Parse(body, pageInfo, path);
            foreach (var item in items(page))
            {
                yield return item;
            }

            next = cursor(page);
            if (next.Length == 0)
            {
                yield break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kalshi client using PRODUCTION environment (read-only)")]
    private static partial void LogProduction(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kalshi GET {Path} attempt {Attempt} failed ({Reason}); retrying in {DelaySeconds:F2}s")]
    private static partial void LogRetry(ILogger logger, string path, int attempt, string reason, double delaySeconds);
}

/// <summary>A 200 response: its body, when the winning attempt was sent and answered, and the raw <c>Date</c> and <c>Age</c> headers.</summary>
/// <param name="Body">The response body.</param>
/// <param name="Sent">Local time just before the successful attempt was sent.</param>
/// <param name="Received">Local time just after its response arrived.</param>
/// <param name="DateHeader">The <c>Date</c> header exactly as sent, or null.</param>
/// <param name="AgeHeader">The <c>Age</c> header exactly as sent, or null.</param>
internal readonly record struct KalshiResponse(string Body, DateTimeOffset Sent, DateTimeOffset Received, string? DateHeader, string? AgeHeader);
