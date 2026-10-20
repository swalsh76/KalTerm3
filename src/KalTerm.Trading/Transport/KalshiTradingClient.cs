using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KalTerm.Core;
using KalTerm.Core.Money;
using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;
using KalTerm.Kalshi.RateLimiting;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Keys;

namespace KalTerm.Trading.Transport;

/// <summary>Settings for <see cref="KalshiTradingClient"/>.</summary>
public sealed class TradingClientOptions
{
    /// <summary>Production is refused unless this is set (the order service sets it only when the risk file enables production).</summary>
    public bool AllowProduction { get; init; }

    /// <summary>The write bucket. When omitted the client builds one from the settings and disposes it; one passed in stays the caller's.</summary>
    public TokenBucket? Bucket { get; init; }

    /// <summary>The clock for waits, signing and lookups; <see cref="TimeProvider.System"/> when omitted.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Tokens an order, amend, decrease or transfer costs. Kalshi's default is 10; read the real figure from <c>/account/endpoint_costs</c>.</summary>
    public decimal DefaultCost { get; init; } = 10m;

    /// <summary>Tokens a cancel costs (per order in a batch). Kalshi's figure is 2.</summary>
    public decimal CancelCost { get; init; } = 2m;

    /// <summary>The pauses before each retry of a rate-limited or idempotent request; its length is the number of retries.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    /// <summary>Per-attempt HTTP timeout. A timeout counts as no answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where every write and its answer is recorded; nothing is recorded when omitted.</summary>
    public ITradingAudit? Audit { get; init; }
}

/// <summary>
/// The write side of the Kalshi API: orders, cancels, amends, shard transfers. A separate client from the read client (which stays GET-only), with its own key. Layers that keep a stray write from happening:
/// it does not exist without a trading signer; it refuses production unless allowed; the transport allows only <see cref="TradingRoutes"/>; and only the order service, holding an <see cref="Risk.ApprovedOrder"/>, builds orders.
/// <para>
/// A write is never resent when its outcome is unclear (timeout, dropped connection, 5xx): an order is looked up by its client order id and, if not found, <see cref="WriteOutcomeUnknownException"/> is thrown; an amend, decrease or transfer
/// is not looked up at all. A 429 means Kalshi did not process it, so every write waits and retries. Cancels are idempotent and also retried after unclear answers. Kalshi does not de-duplicate transfers, so one is sent at most once.
/// </para>
/// </summary>
public sealed class KalshiTradingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TokenBucket _bucket;
    private readonly bool _ownsBucket;
    private readonly TradingSigner _signer;
    private readonly TimeProvider _time;
    private readonly TradingClientOptions _options;
    private readonly string _baseUrl;
    private readonly string _basePath;

    /// <param name="settings">The environment and the rate budgets.</param>
    /// <param name="signer">The trading key's signer, for the same environment.</param>
    /// <param name="options">Options; defaults apply when omitted.</param>
    /// <param name="handler">The HTTP handler; a standard one when omitted. The client takes ownership.</param>
    /// <exception cref="TradingRefusedException">The environment is production and the options do not allow it, or the key is for the other environment.</exception>
    public KalshiTradingClient(KalshiSettings settings, TradingSigner signer, TradingClientOptions? options = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signer);
        options ??= new TradingClientOptions();

        if (settings.IsProduction && !options.AllowProduction)
        {
            throw new TradingRefusedException("the trading client refuses the production environment unless production trading is enabled");
        }

        var environment = settings.IsProduction ? "production" : "demo";
        if (!string.Equals(signer.Environment, environment, StringComparison.Ordinal))
        {
            throw new TradingRefusedException($"the trading key is for {signer.Environment}, not {environment}");
        }

        _signer = signer;
        _options = options;
        _time = options.TimeProvider ?? TimeProvider.System;
        _baseUrl = settings.RestUrl.AbsoluteUri.TrimEnd('/');
        _basePath = settings.RestUrl.AbsolutePath.TrimEnd('/');
        if (options.Bucket is null)
        {
            _bucket = KalshiRateLimits.WriteBucket(settings, _time);
            _ownsBucket = true;
        }
        else
        {
            _bucket = options.Bucket;
        }

        _http = new HttpClient(new RouteGuardHandler(handler ?? new SocketsHttpHandler(), _basePath), disposeHandler: true) { Timeout = options.Timeout };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KalTerm", BuildInfo.Version.Split('+')[0]));
    }

    /// <summary>Sends a new order, exactly once.</summary>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">Cancels waiting; a request already sent is not recalled.</param>
    /// <returns>The created order; <see cref="CreatedOrder.Recovered"/> when it was found by looking.</returns>
    /// <exception cref="WriteOutcomeUnknownException">No clear answer and the order was not found.</exception>
    /// <exception cref="KalshiApiException">Kalshi refused the order.</exception>
    public async Task<CreatedOrder> CreateOrderAsync(OrderRequest order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var body = Json(order.Members());
        var since = _time.GetUtcNow().AddSeconds(-5);
        var reply = await SendAsync(HttpMethod.Post, "/portfolio/events/orders", null, body, _options.DefaultCost, order.ClientOrderId, cancellationToken).ConfigureAwait(false);
        if (reply.Ok)
        {
            return ParseCreated(reply.Body, order.ClientOrderId);
        }

        if (!reply.Ambiguous && reply.Status != (int)HttpStatusCode.Conflict)
        {
            throw reply.ApiError();
        }

        var found = await ResolveAsync(order, since, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            _options.Audit?.Record(_time.GetUtcNow(), "create_order.recovered", order.ClientOrderId, null, found.OrderId);
            return new CreatedOrder(found.OrderId, order.ClientOrderId, found.FillCountFp.Hundredths, found.RemainingCountFp.Hundredths, Recovered: true);
        }

        if (reply.Status == (int)HttpStatusCode.Conflict)
        {
            throw reply.ApiError();
        }

        _options.Audit?.Record(_time.GetUtcNow(), "create_order.unknown", order.ClientOrderId, reply.Status, null);
        throw new WriteOutcomeUnknownException("create_order", order.ClientOrderId);
    }

    /// <summary>Looks for an order by its client order id, retrying a lost answer with growing pauses.</summary>
    /// <param name="clientOrderId">The id sent with the order.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="since">Orders created at or after this moment are listed (whole seconds).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The order, or <see langword="null"/> when none matches.</returns>
    /// <exception cref="HttpRequestException">The network failed through every retry.</exception>
    /// <exception cref="KalshiApiException">Kalshi answered with an error through every retry, or a final one.</exception>
    public async Task<Order?> FindOrderAsync(string clientOrderId, string ticker, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        string? cursor = null;
        while (true)
        {
            var query = KalshiQuery.Empty.With("ticker", ticker).With("min_ts", since.ToUnixTimeSeconds()).With("limit", 1000L).With("cursor", cursor);
            var reply = await SendRepeatedAsync(HttpMethod.Get, "/portfolio/orders", query, null, _options.DefaultCost, clientOrderId, cancellationToken).ConfigureAwait(false);
            if (!reply.Ok)
            {
                throw reply.Status == 0 ? new HttpRequestException($"lookup of {clientOrderId}: {reply.Failure}") : reply.ApiError();
            }

            var page = KalshiJson.Parse(reply.Body, KalshiJsonContext.Default.OrdersPage, "orders");
            if (page.Orders?.FirstOrDefault(o => o.ClientOrderId == clientOrderId) is { } match)
            {
                return match;
            }

            if (page.Cursor.Length == 0)
            {
                return null;
            }

            cursor = page.Cursor;
        }
    }

    /// <summary>Cancels one order; retried after an unclear answer, because cancelling twice is harmless.</summary>
    /// <param name="target">The order, its shard and its market.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result; <see cref="CancelResult.AlreadyGone"/> when a retry found it gone.</returns>
    /// <exception cref="WriteOutcomeUnknownException">Still no clear answer after the retries.</exception>
    /// <exception cref="KalshiApiException">Kalshi refused (including a 404 on the first try: there was no such order).</exception>
    public async Task<CancelResult> CancelOrderAsync(CancelTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var query = KalshiQuery.Empty.With("exchange_index", (long)target.ExchangeIndex).With("market_ticker", target.MarketTicker);
        var reply = await SendRepeatedAsync(HttpMethod.Delete, "/portfolio/events/orders/" + Uri.EscapeDataString(target.OrderId), query, null, _options.CancelCost, target.OrderId, cancellationToken, goneIs404: true).ConfigureAwait(false);
        if (reply.Ambiguous)
        {
            throw new WriteOutcomeUnknownException("cancel_order", target.OrderId);
        }

        if (reply.Gone)
        {
            return new CancelResult(target.OrderId, null, AlreadyGone: true);
        }

        return reply.Ok ? new CancelResult(target.OrderId, ReadContracts(reply.Body, "reduced_by")) : throw reply.ApiError();
    }

    /// <summary>Cancels several orders, each reported on its own; billed per order. Idempotent, so retried.</summary>
    /// <param name="targets">The orders.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>One result per order Kalshi reported, in its order.</returns>
    /// <exception cref="WriteOutcomeUnknownException">Still no clear answer after the retries.</exception>
    public async Task<IReadOnlyList<CancelResult>> CancelOrdersAsync(IReadOnlyList<CancelTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var results = new List<CancelResult>();
        var chunk = Math.Max(1, (int)(_bucket.Capacity / _options.CancelCost));
        foreach (var part in targets.Chunk(chunk))
        {
            var body = Json([new("orders", part.Select(t => new Dictionary<string, object> { ["order_id"] = t.OrderId, ["exchange_index"] = t.ExchangeIndex, ["market_ticker"] = t.MarketTicker }).ToArray())]);
            var label = string.Join(',', part.Select(t => t.OrderId));
            var reply = await SendRepeatedAsync(HttpMethod.Delete, "/portfolio/events/orders/batched", null, body, _options.CancelCost * part.Length, label, cancellationToken).ConfigureAwait(false);
            if (reply.Ambiguous)
            {
                throw new WriteOutcomeUnknownException("cancel_orders", label);
            }

            if (!reply.Ok)
            {
                throw reply.ApiError();
            }

            using var doc = JsonDocument.Parse(reply.Body);
            if (doc.RootElement.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in orders.EnumerateArray())
                {
                    var error = item.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? (e.TryGetProperty("code", out var code) ? code.GetString() : null) ?? "error" : null;
                    results.Add(new CancelResult(Text(item, "order_id") ?? string.Empty, ReadContracts(item, "reduced_by"), Error: error));
                }
            }
        }

        return results;
    }

    /// <summary>Cancels every resting order. Idempotent, so retried.</summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A task that completes when Kalshi has accepted the request.</returns>
    /// <exception cref="WriteOutcomeUnknownException">Still no clear answer after the retries.</exception>
    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendRepeatedAsync(HttpMethod.Delete, "/portfolio/events/orders", null, null, _options.CancelCost, "all", cancellationToken).ConfigureAwait(false);
        if (reply.Ambiguous)
        {
            throw new WriteOutcomeUnknownException("cancel_all", "all");
        }

        if (!reply.Ok)
        {
            throw reply.ApiError();
        }
    }

    /// <summary>Changes the price and size of a resting order. Never resent after an unclear answer.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="side">The side of the YES book.</param>
    /// <param name="priceE6">The new price in millionths.</param>
    /// <param name="countE2">The new size in hundredths.</param>
    /// <param name="exchangeIndex">The shard.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The changed order.</returns>
    /// <exception cref="WriteOutcomeUnknownException">No clear answer; look at the order before doing anything else.</exception>
    public async Task<ChangedOrder> AmendOrderAsync(string orderId, string ticker, BookSide side, long priceE6, long countE2, int exchangeIndex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countE2);
        if (priceE6 is <= 0 or >= 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priceE6), priceE6, "the price must be strictly between $0 and $1");
        }

        var body = Json(
        [
            new("ticker", ticker),
            new("side", side == BookSide.Bid ? "bid" : "ask"),
            new("price", FixedPointText.Price(priceE6)),
            new("count", FixedPointText.Count(countE2)),
            new("exchange_index", exchangeIndex),
        ]);
        return await ChangeAsync("amend_order", orderId, "/amend", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes contracts off a resting order, by an amount or down to an amount. Never resent after an unclear answer.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="exchangeIndex">The shard.</param>
    /// <param name="marketTicker">The market.</param>
    /// <param name="reduceByE2">Contracts to remove, in hundredths; give this or <paramref name="reduceToE2"/>.</param>
    /// <param name="reduceToE2">Contracts to leave, in hundredths.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The changed order.</returns>
    /// <exception cref="WriteOutcomeUnknownException">No clear answer; look at the order before doing anything else.</exception>
    public async Task<ChangedOrder> DecreaseOrderAsync(string orderId, int exchangeIndex, string marketTicker, long? reduceByE2 = null, long? reduceToE2 = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(marketTicker);
        if ((reduceByE2 is null) == (reduceToE2 is null))
        {
            throw new ArgumentException("give exactly one of reduce_by and reduce_to");
        }

        var members = new List<KeyValuePair<string, object>> { new("exchange_index", exchangeIndex), new("market_ticker", marketTicker) };
        members.Add(reduceByE2 is { } by ? new("reduce_by", FixedPointText.Count(by, "reduce_by")) : new("reduce_to", FixedPointText.Count(reduceToE2!.Value, "reduce_to")));
        return await ChangeAsync("decrease_order", orderId, "/decrease", Json(members), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves collateral between two shards of the account. Sent at most once: Kalshi does not de-duplicate a transfer id (measured 2026-10-13), so an unclear answer is never resent.</summary>
    /// <param name="sourceShard">The shard the money leaves.</param>
    /// <param name="destinationShard">The shard it goes to.</param>
    /// <param name="amountE6">The amount in millionths of a dollar: positive, a whole number of centicents.</param>
    /// <param name="transferId">An id for this transfer.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Kalshi's transfer id.</returns>
    /// <exception cref="WriteOutcomeUnknownException">No clear answer; read the balance before doing anything else.</exception>
    public async Task<string> TransferAsync(int sourceShard, int destinationShard, long amountE6, string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        if (sourceShard == destinationShard)
        {
            throw new ArgumentException("the source and the destination shard must differ");
        }

        if (amountE6 <= 0 || amountE6 % 100 != 0)
        {
            throw new ArgumentException("the amount must be positive and a whole number of centicents ($0.0001)", nameof(amountE6));
        }

        var body = Json(
        [
            new("source", "event_contract"),
            new("destination", "event_contract"),
            new("amount", amountE6 / 100),
            new("client_transfer_id", transferId),
            new("source_exchange_shard", sourceShard),
            new("destination_exchange_shard", destinationShard),
        ]);
        var reply = await SendAsync(HttpMethod.Post, "/portfolio/intra_exchange_instance_transfer", null, body, _options.DefaultCost, transferId, cancellationToken).ConfigureAwait(false);
        if (reply.Ambiguous)
        {
            _options.Audit?.Record(_time.GetUtcNow(), "transfer.unknown", transferId, reply.Status, null);
            throw new WriteOutcomeUnknownException("transfer", transferId);
        }

        if (!reply.Ok)
        {
            throw reply.ApiError();
        }

        using var doc = JsonDocument.Parse(reply.Body);
        return Text(doc.RootElement, "transfer_id") ?? throw new KalshiResponseException("transfer: the reply has no transfer_id");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        if (_ownsBucket)
        {
            _bucket.Dispose();
        }
    }

    private static string Json(IEnumerable<KeyValuePair<string, object>> members)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in members)
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case Dictionary<string, object> map:
                writer.WriteStartObject();
                foreach (var (name, inner) in map)
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, inner);
                }

                writer.WriteEndObject();
                break;
            case Array array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteValue(writer, item!);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException($"cannot write a {value.GetType().Name} into a request body");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadContracts(JsonElement element, string name) =>
        Text(element, name) is { } text && Contracts.TryParse(text, out var count) ? count.Hundredths : null;

    private static long? ReadContracts(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadContracts(doc.RootElement, name) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CreatedOrder ParseCreated(string body, string clientOrderId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new CreatedOrder(
                Text(root, "order_id") ?? throw new KalshiResponseException("create order: the reply has no order_id"),
                Text(root, "client_order_id") ?? clientOrderId,
                ReadContracts(root, "fill_count") ?? 0,
                ReadContracts(root, "remaining_count") ?? 0);
        }
        catch (JsonException ex)
        {
            throw new KalshiResponseException("create order: " + ex.Message, ex);
        }
    }

    private async Task<ChangedOrder> ChangeAsync(string kind, string orderId, string suffix, string body, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(HttpMethod.Post, "/portfolio/events/orders/" + Uri.EscapeDataString(orderId) + suffix, null, body, _options.DefaultCost, orderId, cancellationToken).ConfigureAwait(false);
        if (reply.Ambiguous)
        {
            _options.Audit?.Record(_time.GetUtcNow(), kind + ".unknown", orderId, reply.Status, null);
            throw new WriteOutcomeUnknownException(kind, orderId);
        }

        if (!reply.Ok)
        {
            throw reply.ApiError();
        }

        using var doc = JsonDocument.Parse(reply.Body);
        return new ChangedOrder(Text(doc.RootElement, "order_id") ?? orderId, ReadContracts(doc.RootElement, "remaining_count"));
    }

    /// <summary>After an unclear answer: look for the order, now and again after each pause.</summary>
    private async Task<Order?> ResolveAsync(OrderRequest order, DateTimeOffset since, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _options.RetryDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_options.RetryDelays[attempt - 1], _time, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (await FindOrderAsync(order.ClientOrderId, order.Ticker, since, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or KalshiApiException)
            {
                // the lookup itself failed; try again after the next pause, and give up as unknown after the last
            }
        }

        return null;
    }

    /// <summary>One request, retried only while Kalshi says 429 (it did not process it).</summary>
    private async Task<Reply> SendAsync(HttpMethod method, string path, KalshiQuery? query, string? body, decimal cost, string reference, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            var reply = await AttemptAsync(method, path, query, body, cost, reference, cancellationToken).ConfigureAwait(false);
            if (reply.Status != (int)HttpStatusCode.TooManyRequests || retry >= _options.RetryDelays.Count)
            {
                return reply;
            }

            await Task.Delay(_options.RetryDelays[retry], _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A request that is safe to repeat: retried after a 429 and after any unclear answer, then given up on.</summary>
    private async Task<Reply> SendRepeatedAsync(HttpMethod method, string path, KalshiQuery? query, string? body, decimal cost, string reference, CancellationToken cancellationToken, bool goneIs404 = false)
    {
        var reply = default(Reply);
        for (var retry = 0; retry <= _options.RetryDelays.Count; retry++)
        {
            if (retry > 0)
            {
                await Task.Delay(_options.RetryDelays[retry - 1], _time, cancellationToken).ConfigureAwait(false);
            }

            reply = await SendAsync(method, path, query, body, cost, reference, cancellationToken).ConfigureAwait(false);
            if (!reply.Ambiguous)
            {
                return goneIs404 && retry > 0 && reply.Status == (int)HttpStatusCode.NotFound ? reply with { Gone = true } : reply;
            }
        }

        return reply;
    }

    private async Task<Reply> AttemptAsync(HttpMethod method, string path, KalshiQuery? query, string? body, decimal cost, string reference, CancellationToken cancellationToken)
    {
        await _bucket.AcquireAsync(cost, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, new Uri(_baseUrl + path + (query ?? KalshiQuery.Empty).ToQueryString(), UriKind.Absolute));
        foreach (var header in _signer.Signer.Headers(method.Method, _basePath + path))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        _options.Audit?.Record(_time.GetUtcNow(), method.Method + " " + path, reference, null, null);
        Reply reply;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            reply = new Reply((int)response.StatusCode, text, string.Empty);
        }
        catch (TradingRefusedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            reply = new Reply(0, string.Empty, ex.GetType().Name + ": " + ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reply = new Reply(0, string.Empty, "timeout");
        }

        _options.Audit?.Record(_time.GetUtcNow(), "reply " + method.Method + " " + path, reference, reply.Status, null);
        return reply;
    }

    /// <summary>What came back: a status (0 when nothing did) and the body.</summary>
    private readonly record struct Reply(int Status, string Body, string Failure, bool Gone = false)
    {
        public bool Ok => Status is >= 200 and < 300;

        /// <summary>No answer, or one that does not say whether it was processed.</summary>
        public bool Ambiguous => Status is 0 or 500 or 502 or 503 or 504;

        public KalshiApiException ApiError() => new(Status, Body);
    }
}
