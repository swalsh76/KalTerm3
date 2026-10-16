using System.Net;
using System.Text;

namespace KalTerm.TestSupport;

/// <summary>What the stub saw: the method, the URL and a copy of the headers (the request itself is disposed after sending).</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The full URL including the query.</param>
/// <param name="Headers">Request headers, name to joined values.</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> for tests: each call answers with the next scripted response or throws the
/// next scripted exception, and every request is recorded. When the script runs out the call fails loudly instead of
/// quietly succeeding, so an unexpected extra request is a test failure. The role the reference fills with <c>respx</c>.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _sync = new();

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answers the next call with <paramref name="status"/> and a JSON body.</summary>
    public StubHttpMessageHandler Respond(HttpStatusCode status, string body = "{}", IReadOnlyDictionary<string, string>? headers = null) =>
        RespondWith(_ => Response(status, body, headers));

    /// <summary>Answers the next call with whatever <paramref name="build"/> returns for the request.</summary>
    public StubHttpMessageHandler RespondWith(Func<HttpRequestMessage, HttpResponseMessage> build)
    {
        lock (_sync)
        {
            _script.Enqueue(build);
        }

        return this;
    }

    /// <summary>Makes the next call throw <paramref name="exception"/>.</summary>
    public StubHttpMessageHandler Throw(Exception exception) =>
        RespondWith(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Func<HttpRequestMessage, HttpResponseMessage> next;
        lock (_sync)
        {
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (!_script.TryDequeue(out next!))
            {
                throw new InvalidOperationException($"no scripted response for {request.Method} {request.RequestUri}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(next(request));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, IReadOnlyDictionary<string, string>? headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var header in headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }
}
