namespace KalTerm.Kalshi.Rest;

/// <summary>
/// The outermost layer of the read client's HTTP pipeline: it refuses every request that is not a GET before anything is
/// sent. Read-only is enforced structurally (the client has no write methods) and again here, at the transport.
/// The write path is a separate trading client with its own key; removing this guard is never the way to get one.
/// </summary>
internal sealed class ReadOnlyGuardHandler : DelegatingHandler
{
    public ReadOnlyGuardHandler(HttpMessageHandler inner)
        : base(inner)
    {
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

    private static void Guard(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method != HttpMethod.Get)
        {
            throw new ReadOnlyViolationException($"{request.Method} {request.RequestUri?.AbsolutePath} blocked: client is read-only");
        }
    }
}
