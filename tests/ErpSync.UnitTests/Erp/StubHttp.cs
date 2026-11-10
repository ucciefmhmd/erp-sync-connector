using System.Net;
using System.Text;

namespace ErpSync.UnitTests.Erp;

/// <summary>An HTTP handler that answers from a script and records every request it receives.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public static StubHandler Json(HttpStatusCode status, string body) =>
        new((_, _) => Response(status, body));

    public static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
            body));

        return respond(request, Requests.Count);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? IdempotencyKey, string? Body);

/// <summary>Hands out clients over a fixed handler, like a configured named HttpClient.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://erp.test/") };
}

/// <summary>Throws a given exception instead of answering, to simulate transport failures.</summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(exception);
}
