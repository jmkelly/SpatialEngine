using System.Net;
using System.Text;

namespace Spatial.Client.Tests;

/// <summary>
/// A scriptable <see cref="HttpMessageHandler"/> for client SDK tests: each
/// request is answered by a responder function, and every request/response is
/// recorded so tests can assert URLs, methods and request bodies.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    public sealed record Exchange(HttpRequestMessage Request, HttpResponseMessage Response);

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly List<Exchange> _exchanges = [];

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public IReadOnlyList<Exchange> Exchanges
    {
        get
        {
            lock (_exchanges)
            {
                return _exchanges.ToArray();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = _responder(request);
        lock (_exchanges)
        {
            _exchanges.Add(new Exchange(request, response));
        }

        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    public static HttpResponseMessage Ndjson(string ndjson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
        };
        return response;
    }
}

/// <summary>Convenience wrapper: a client over the stub, with the recorded exchanges readable.</summary>
internal sealed class StubClient : IDisposable
{
    public StubClient(StubHttpHandler handler)
    {
        Handler = handler;
        Http = new HttpClient(handler) { BaseAddress = new Uri("http://spatial.test") };
        Client = new SpatialClient(Http);
    }

    public StubHttpHandler Handler { get; }

    public HttpClient Http { get; }

    public SpatialClient Client { get; }

    public void Dispose() => Http.Dispose();
}
