using System.Text.RegularExpressions;

namespace KalTerm.Trading.Transport;

/// <summary>The trading client was asked to do something it is built never to do.</summary>
public sealed class TradingRefusedException : Exception
{
    public TradingRefusedException()
    {
    }

    public TradingRefusedException(string message)
        : base(message)
    {
    }

    public TradingRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A write got no clear answer and could not be settled by looking: it may have happened. <see cref="Kind"/> says what ("create_order", "amend_order", "transfer", ...),
/// <see cref="Reference"/> is the id to reconcile by. Never retry with a new id before looking.
/// </summary>
public sealed class WriteOutcomeUnknownException : Exception
{
    public WriteOutcomeUnknownException()
    {
    }

    public WriteOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public WriteOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WriteOutcomeUnknownException(string kind, string reference)
        : base($"{kind} {reference}: no clear answer from Kalshi; it may have taken effect")
    {
        Kind = kind;
        Reference = reference;
    }

    /// <summary>What was being done.</summary>
    public string Kind { get; } = string.Empty;

    /// <summary>The id to reconcile by (a client order id, an order id or a transfer id).</summary>
    public string Reference { get; } = string.Empty;
}

/// <summary>
/// The only routes the trading client can reach, by method and path (below the API's base path). Anything else (withdrawals, account changes, subaccount transfers, the old order routes)
/// is refused before it is sent, however the code around it changes.
/// </summary>
public static class TradingRoutes
{
    private static readonly (HttpMethod Method, Regex Path)[] Allowed =
    [
        (HttpMethod.Post, Route("/portfolio/events/orders")),
        (HttpMethod.Delete, Route("/portfolio/events/orders")),
        (HttpMethod.Delete, Route("/portfolio/events/orders/batched")),
        (HttpMethod.Delete, Route("/portfolio/events/orders/[^/]+")),
        (HttpMethod.Post, Route("/portfolio/events/orders/[^/]+/amend")),
        (HttpMethod.Post, Route("/portfolio/events/orders/[^/]+/decrease")),
        (HttpMethod.Post, Route("/portfolio/intra_exchange_instance_transfer")),
        (HttpMethod.Get, Route("/portfolio/orders")),   // only to find an order after an unclear answer
    ];

    /// <summary>Whether the request is one of the allowed routes.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path below the base path, without the query.</param>
    /// <returns><see langword="true"/> when it is allowed.</returns>
    public static bool IsAllowed(HttpMethod method, string path)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        return Array.Exists(Allowed, route => route.Method == method && route.Path.IsMatch(path));
    }

    private static Regex Route(string pattern) => new("^" + pattern + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>The outermost layer of the trading client's pipeline: refuses every request outside <see cref="TradingRoutes"/> before anything is sent.</summary>
public sealed class RouteGuardHandler : DelegatingHandler
{
    private readonly string _basePath;

    /// <param name="inner">Where allowed requests go.</param>
    /// <param name="basePath">The API base path, for example <c>/trade-api/v2</c>.</param>
    public RouteGuardHandler(HttpMessageHandler inner, string basePath)
        : base(inner)
    {
        _basePath = basePath;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Guard(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Guard(request);
        return base.Send(request, cancellationToken);
    }

    private void Guard(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var below = path.StartsWith(_basePath, StringComparison.Ordinal) ? path[_basePath.Length..] : path;
        if (!TradingRoutes.IsAllowed(request.Method, below))
        {
            throw new TradingRefusedException($"{request.Method} {below} is not a route the trading client may use");
        }
    }
}
