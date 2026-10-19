using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>
/// <see cref="IKalTermApi"/> over HTTP. Read-only: it only ever sends <c>GET</c>. A refusal from the API becomes an <see cref="ApiException"/> carrying the
/// API's own error code; a connection that fails or times out becomes an <see cref="ApiUnavailableException"/> naming the address.
/// </summary>
public sealed class KalTermApiClient : IKalTermApi, IDisposable
{
    /// <summary>Where the API listens unless told otherwise: loopback, port 5280.</summary>
    public const string DefaultBaseAddress = "http://127.0.0.1:5280";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>Wraps an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/> is set. The caller keeps ownership of it.</summary>
    /// <param name="http">The client.</param>
    public KalTermApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("the HttpClient needs a BaseAddress", nameof(http));
        }

        _http = http;
    }

    private KalTermApiClient(HttpClient http, bool ownsClient)
        : this(http)
    {
        _ownsClient = ownsClient;
    }

    /// <summary>Creates a client for an address; the client owns its <see cref="HttpClient"/>.</summary>
    /// <param name="baseAddress">The API's address, for example <see cref="DefaultBaseAddress"/>.</param>
    /// <param name="handler">The HTTP handler (for tests); a standard one when <see langword="null"/>. The client takes ownership of it.</param>
    /// <param name="timeout">How long one call may take; 30 seconds when <see langword="null"/>.</param>
    /// <returns>The client.</returns>
    public static KalTermApiClient Create(Uri baseAddress, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true) { BaseAddress = baseAddress, Timeout = timeout ?? DefaultTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KalTerm-Client/1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return new KalTermApiClient(http, ownsClient: true);
    }

    /// <inheritdoc/>
    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync("v1/status", [], ApiJson.Wire.StatusDto, cancellationToken);

    /// <inheritdoc/>
    public Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync(
            "v1/markets",
            [("status", query.Status), ("event", query.Event), ("q", query.Search), ("limit", Number(query.Limit)), ("cursor", query.Cursor)],
            ApiJson.Wire.MarketPage,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default) =>
        GetAsync($"v1/markets/{Segment(ticker)}", [], ApiJson.Wire.MarketDto, cancellationToken);

    /// <inheritdoc/>
    public Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync($"v1/markets/{Segment(ticker)}/ticks", Window(query), ApiJson.Wire.TickPage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync($"v1/markets/{Segment(ticker)}/trades", Window(query), ApiJson.Wire.TradePage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync("v1/gaps", [("start", Moment(query.Start)), ("end", Moment(query.End)), ("limit", Number(query.Limit))], ApiJson.Wire.GapPage, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private static (string, string?)[] Window(WindowQuery query) =>
        [("start", Moment(query.Start)), ("end", Moment(query.End)), ("limit", Number(query.Limit)), ("cursor", query.Cursor)];

    private static string? Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string? Moment(DateTime? value) => value is { } moment ? ApiFormat.Moment(moment) : null;

    private static string Segment(string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        return Uri.EscapeDataString(ticker);
    }

    private static string Url(string path, IEnumerable<(string Name, string? Value)> parameters)
    {
        var pairs = parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToList();
        return pairs.Count == 0 ? path : $"{path}?{string.Join('&', pairs)}";
    }

    private async Task<T> GetAsync<T>(string path, IEnumerable<(string Name, string? Value)> parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
        where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(new Uri(Url(path, parameters), UriKind.Relative), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiUnavailableException($"could not reach the API at {_http.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiUnavailableException($"the API at {_http.BaseAddress} did not answer in time", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Refusal(response.StatusCode, body);
            }

            try
            {
                return JsonSerializer.Deserialize(body, info) ?? throw new ApiException(response.StatusCode, "bad_response");
            }
            catch (JsonException)
            {
                throw new ApiException(response.StatusCode, "bad_response");
            }
        }
    }

    private static ApiException Refusal(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            if (JsonSerializer.Deserialize(body, ApiJson.Wire.ErrorDto) is { Error.Length: > 0 } error)
            {
                return new ApiException(status, error.Error, error.Fields);
            }
        }
        catch (JsonException)
        {
            // Not the API's own error body (a proxy's page, an empty reply): report it by status alone.
        }

        return new ApiException(status, "http_error");
    }
}
