using System.Net;
using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Factory helpers for UI HTTP-service tests: standalone client-factory / token-storage doubles for
/// tests that wire the pieces individually instead of through <see cref="UiHttpServiceHarness"/>,
/// plus the canned-response builders shared by both styles.
/// </summary>
public static class HttpTestDoubles
{
    /// <summary>Base address applied to test clients so services can use relative URIs.</summary>
    public static readonly Uri BaseAddress = UiHttpServiceHarness.DefaultBaseAddress;

    /// <summary>
    /// Builds an <see cref="IHttpClientFactory"/> that returns a fresh <see cref="HttpClient"/> per
    /// call (services dispose the client after each request) wired to the given shared handler.
    /// </summary>
    /// <param name="handler">The shared handler (typically a <see cref="CapturingHttpMessageHandler"/>).</param>
    /// <param name="baseAddress">The client base address (defaults to <see cref="BaseAddress"/>).</param>
    public static IHttpClientFactory ClientFactory(HttpMessageHandler handler, Uri? baseAddress = null) =>
        new FreshApiClientFactory(handler, baseAddress ?? BaseAddress);

    /// <summary>Builds a token storage stub returning the given access token (or none when null).</summary>
    /// <param name="accessToken">The canned access token.</param>
    public static ITokenStorageService TokenStorage(string? accessToken = "test-token") =>
        new StubTokenStorageService(accessToken);

    /// <summary>Creates a JSON response with the given payload (web serializer defaults).</summary>
    /// <typeparam name="T">The payload type to serialize.</typeparam>
    public static HttpResponseMessage JsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(payload) };

    /// <summary>Creates a body-less response with the given status code.</summary>
    public static HttpResponseMessage EmptyResponse(HttpStatusCode statusCode = HttpStatusCode.NoContent) =>
        new(statusCode);

    /// <summary>
    /// Creates a ProblemDetails-style error response the way the WebAPI emits domain failures, so the
    /// UI-side error mapping (e.g. a ServiceExceptionHelper) sees the shape it expects.
    /// </summary>
    public static HttpResponseMessage ProblemResponse(
        string detail,
        string title = "Domain Exception",
        HttpStatusCode statusCode = HttpStatusCode.BadRequest) =>
        JsonResponse(new { title, detail }, statusCode);
}
