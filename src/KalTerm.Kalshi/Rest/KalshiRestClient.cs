using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Core;
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

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsBucket)
        {
            _bucket.Dispose();
        }
    }

    /// <summary>
    /// Sends a GET and returns the body of the 200 response. This is the single place requests leave the client: it paces,
    /// signs, sends and retries.
    /// </summary>
    internal async Task<string> GetStringAsync(string path, KalshiQuery? query, CancellationToken cancellationToken)
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
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return body;
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
