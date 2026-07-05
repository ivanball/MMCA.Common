using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Immutable snapshot of a request seen by <see cref="StubHttpMessageHandler"/>, captured before
/// the canned response is produced (method, absolute URI, serialized body, and auth header).
/// </summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Body,
    AuthenticationHeaderValue? Authorization);

/// <summary>
/// Test-local capturing <see cref="HttpMessageHandler"/> with canned responses. There is no shared
/// fake handler in the MMCA.Common.Testing packages (each test project hand-rolls its own, mirroring
/// the API.Tests pattern), so the UI service tests share this one.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public CapturedRequest LastRequest => Requests[^1];

    /// <summary>Creates a handler that answers every request with the given status and optional JSON body.</summary>
    public static StubHttpMessageHandler RespondingWith(HttpStatusCode statusCode, string? json = null) =>
        new(_ => CreateResponse(statusCode, json));

    /// <summary>Builds a canned response with an optional application/json body.</summary>
    public static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? json = null) =>
        json is null
            ? new HttpResponseMessage(statusCode)
            : new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body, request.Headers.Authorization));
        return responder(request);
    }
}

/// <summary>
/// <see cref="IHttpClientFactory"/> double that hands out clients bound to a single
/// <see cref="StubHttpMessageHandler"/> and records the requested client name.
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public string? LastClientName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastClientName = name;
        return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }
}
