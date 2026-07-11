using System.Net;
using System.Text;
using System.Text.Json;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Canned-response, request-capturing <see cref="HttpMessageHandler"/> for unit-testing HTTP-backed
/// UI services without a server. Supports two configuration styles: a responder delegate (ctor)
/// invoked once per request so repeated calls get fresh responses, and route registration via
/// <see cref="SetResponse"/> (HTTP method plus absolute path, query string ignored, last
/// registration wins). Registered routes are consulted first; unmatched requests fall through to the
/// responder when one was supplied, otherwise 404 with an empty body, which mirrors the WebAPI's
/// not-found behavior and keeps incidental refresh calls out of each test's setup. Responses are
/// built fresh per request so a Polly retry pipeline never reuses a consumed
/// <see cref="HttpContent"/>. Every request is recorded (method, URI, Authorization header, body).
/// </summary>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _respond;
    private readonly List<Route> _routes = [];
    private readonly List<CapturedRequest> _requests = [];

    /// <summary>
    /// Route-registration mode: register canned responses via <see cref="SetResponse"/>; unmatched
    /// requests return 404.
    /// </summary>
    public CapturingHttpMessageHandler()
    {
    }

    /// <summary>
    /// Responder-delegate mode: <paramref name="respond"/> answers every request not matched by a
    /// registered route, and is invoked once per request so repeated calls get fresh responses.
    /// </summary>
    public CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Every request the services sent, in order.</summary>
    public IReadOnlyList<CapturedRequest> Requests => _requests;

    /// <summary>
    /// Registers a canned response for the given method + absolute path (e.g. <c>"/orders/42/checkout"</c>).
    /// <paramref name="body"/> may be <see langword="null"/> (empty body), a raw JSON <see cref="string"/>,
    /// or any object (serialized with web defaults, matching what the WebAPI sends).
    /// </summary>
    public void SetResponse(HttpMethod method, string absolutePath, HttpStatusCode statusCode, object? body = null)
    {
        var json = body switch
        {
            null => null,
            string raw => raw,
            _ => JsonSerializer.Serialize(body, WebJson),
        };
        _routes.Add(new Route(method, absolutePath, statusCode, json));
    }

    /// <summary>All captured requests matching the given method + absolute path.</summary>
    public IReadOnlyList<CapturedRequest> RequestsFor(HttpMethod method, string absolutePath) =>
    [
        .. _requests.Where(r =>
            r.Method == method && string.Equals(r.Path, absolutePath, StringComparison.OrdinalIgnoreCase)),
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(await CaptureAsync(request, cancellationToken));
        return Respond(request);
    }

    private static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var uri = request.RequestUri;
        return new CapturedRequest(
            request.Method,
            uri,
            uri?.AbsolutePath ?? string.Empty,
            uri?.PathAndQuery ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            body);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath;
        var route = path is null
            ? null
            : _routes.LastOrDefault(r =>
                r.Method == request.Method && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (route is not null)
        {
            return route.ToResponse();
        }

        if (_respond is not null)
        {
            return _respond(request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private sealed record Route(HttpMethod Method, string Path, HttpStatusCode StatusCode, string? JsonBody)
    {
        public HttpResponseMessage ToResponse()
        {
            var response = new HttpResponseMessage(StatusCode);
            if (JsonBody is not null)
            {
                response.Content = new StringContent(JsonBody, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}

/// <summary>
/// Immutable snapshot of a captured HTTP request: method, full URI, absolute path, path + query,
/// Authorization header, and body text (<see langword="null"/> when the request had no content).
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string Path,
    string PathAndQuery,
    string? Authorization,
    string? Body);
