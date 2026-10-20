using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>
/// <see cref="IKalTermApi"/> over HTTP. Market data is read with <c>GET</c> only; the single other thing it sends is the caller's own watchlists, alerts and time zone to the KalTerm server
/// (<c>POST</c>, <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> under <c>/v1/watchlists</c>, <c>/v1/alerts</c> and <c>/v1/me/settings</c>, and nothing else). It never talks to Kalshi. A refusal from the API becomes an <see cref="ApiException"/> carrying the
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
    /// <param name="token">The API token to send with every call; none when <see langword="null"/>.</param>
    /// <returns>The client.</returns>
    public static KalTermApiClient Create(Uri baseAddress, HttpMessageHandler? handler = null, TimeSpan? timeout = null, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true) { BaseAddress = baseAddress, Timeout = timeout ?? DefaultTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KalTerm-Client/1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        }
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
    public Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync("v1/screener", [("sort", query.Sort), ("order", query.Order), ("status", query.Status), ("min_volume", query.MinVolume), ("limit", Number(query.Limit)), ("cursor", query.Cursor), ("watchlist", query.Watchlist)], ApiJson.Wire.ScreenerPage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("v1/watchlists", [], ApiJson.Wire.WatchlistListDto, cancellationToken);

    /// <inheritdoc/>
    public Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default) =>
        GetAsync($"v1/watchlists/{Segment(name)}", [], ApiJson.Wire.WatchlistDto, cancellationToken);

    /// <inheritdoc/>
    public async Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default) =>
        Read(await SendAsync(HttpMethod.Post, "v1/watchlists", JsonSerializer.Serialize(new CreateWatchlistRequest(name), ApiJson.Wire.CreateWatchlistRequest), cancellationToken).ConfigureAwait(false), ApiJson.Wire.WatchlistDto);

    /// <inheritdoc/>
    public async Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Delete, $"v1/watchlists/{Segment(name)}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Put, $"v1/watchlists/{Segment(name)}/markets/{Segment(ticker)}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Delete, $"v1/watchlists/{Segment(name)}/markets/{Segment(ticker)}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("v1/alerts", [], ApiJson.Wire.AlertListDto, cancellationToken);

    /// <inheritdoc/>
    public async Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(await SendAsync(HttpMethod.Post, "v1/alerts", JsonSerializer.Serialize(request, ApiJson.Wire.CreateAlertRequest), cancellationToken).ConfigureAwait(false), ApiJson.Wire.AlertDto);
    }

    /// <inheritdoc/>
    public async Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Patch, $"v1/alerts/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}", JsonSerializer.Serialize(new PatchAlertRequest(paused), ApiJson.Wire.PatchAlertRequest), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Delete, $"v1/alerts/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        GetAsync("v1/alerts/fires", [("after", after?.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("limit", Number(limit))], ApiJson.Wire.AlertFiresPage, cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Url("v1/alerts/stream", [("after", after?.ToString(System.Globalization.CultureInfo.InvariantCulture))]), UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            if (!response.IsSuccessStatusCode)
            {
                throw Refusal(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            await foreach (var (type, id, data) in ServerSentEvents.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                if (!long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    continue;
                }

                if (type == "ready")
                {
                    yield return new AlertStreamEvent(number, null);
                }
                else if (type == "fire" && JsonSerializer.Deserialize(data, ApiJson.Wire.AlertFireDto) is { } fire)
                {
                    yield return new AlertStreamEvent(number, fire);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MarketStreamEvent> StreamMarketsAsync(MarketStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new List<(string Name, string? Value)>
        {
            ("ticks", request.Ticks.Count == 0 ? null : string.Join(',', request.Ticks)),
            ("books", request.Books.Count == 0 ? null : string.Join(',', request.Books)),
            ("depth", request.Depth?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("after", request.ResumeFrom),
        };
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(Url("v1/stream", parameters), UriKind.Relative));
        message.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            if (!response.IsSuccessStatusCode)
            {
                throw Refusal(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            await foreach (var (type, id, data) in ServerSentEvents.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                MarketStreamEvent? item = type switch
                {
                    "ready" => JsonSerializer.Deserialize(data, ApiJson.Wire.StreamReadyDto) is { } ready ? new ReadyEvent(ready) : null,
                    "book" => JsonSerializer.Deserialize(data, ApiJson.Wire.OrderBookDto) is { } book ? new BookEvent(book) : null,
                    "tick" when id is not null => JsonSerializer.Deserialize(data, ApiJson.Wire.MarketTickDto) is { } tick ? new TickEvent(tick, id) : null,
                    "reset" => JsonSerializer.Deserialize(data, ApiJson.Wire.StreamResetDto) is { } reset ? new ResetEvent(reset.Reason) : null,
                    "subscribed" => JsonSerializer.Deserialize(data, ApiJson.Wire.StreamSubscriptionDto) is { } added ? new SubscribedEvent(added) : null,
                    "unsubscribed" => JsonSerializer.Deserialize(data, ApiJson.Wire.StreamSubscriptionDto) is { } removed ? new UnsubscribedEvent(removed) : null,
                    _ => null,
                };
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task AddStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Put, StreamChangePath(streamId, kind, ticker), null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task RemoveStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Delete, StreamChangePath(streamId, kind, ticker), null, cancellationToken).ConfigureAwait(false);

    private static string StreamChangePath(string streamId, StreamKind kind, string ticker) =>
        $"v1/stream/{Segment(streamId)}/{(kind == StreamKind.Books ? "books" : "ticks")}/{Segment(ticker)}";

    /// <inheritdoc/>
    public Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default) =>
        GetAsync("v1/me/settings/time_zone", [], ApiJson.Wire.SettingDto, cancellationToken);

    /// <inheritdoc/>
    public async Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default)
    {
        if (zone is null)
        {
            await SendAsync(HttpMethod.Delete, "v1/me/settings/time_zone", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAsync(HttpMethod.Put, "v1/me/settings/time_zone", JsonSerializer.Serialize(new SettingValueRequest(zone), ApiJson.Wire.SettingValueRequest), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default) =>
        GetAsync($"v1/markets/{Segment(ticker)}/metrics", [], ApiJson.Wire.MetricsDto, cancellationToken);

    /// <inheritdoc/>
    public Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync($"v1/markets/{Segment(ticker)}/ticks", Window(query), ApiJson.Wire.TickPage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync($"v1/markets/{Segment(ticker)}/candles", [("interval", interval), .. Window(query)], ApiJson.Wire.CandlePage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync($"v1/markets/{Segment(ticker)}/trades", Window(query), ApiJson.Wire.TradePage, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default) =>
        GetAsync("v1/orderbooks", [("depth", Number(depth))], ApiJson.Wire.OrderBookPage, cancellationToken);

    /// <inheritdoc/>
    public Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default) =>
        GetAsync($"v1/markets/{Segment(ticker)}/orderbook", [("depth", Number(depth))], ApiJson.Wire.OrderBookDto, cancellationToken);

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

    private static T Read<T>(string body, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info) ?? throw new ApiException(System.Net.HttpStatusCode.OK, "bad_response");
        }
        catch (JsonException)
        {
            throw new ApiException(System.Net.HttpStatusCode.OK, "bad_response");
        }
    }

    /// <summary>Sends a change to the caller's own data and returns the reply body (empty for a 204).</summary>
    private async Task<string> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            return response.IsSuccessStatusCode ? body : throw Refusal(response.StatusCode, body);
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
