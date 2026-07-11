using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Owns the disposable HTTP plumbing shared by UI HTTP-service tests across repos: the
/// canned-response capturing <see cref="CapturingHttpMessageHandler"/>, an
/// <see cref="IHttpClientFactory"/> that hands out a fresh <c>"APIClient"</c>
/// <see cref="HttpClient"/> per call (the services dispose each client after use, so the factory
/// must never return the same instance twice), and a token storage stub returning a fixed bearer
/// token. Configure canned responses either through the responder-delegate ctor or via
/// <c>Handler.SetResponse(...)</c> routes (unmatched requests return 404).
/// </summary>
public sealed class UiHttpServiceHarness : IDisposable
{
    /// <summary>Default base address applied to every created client so services can use relative URIs.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://gateway.test/");

    /// <summary>
    /// Creates the harness in route-registration mode: register canned responses via
    /// <c>Handler.SetResponse(...)</c>; unmatched requests return 404.
    /// </summary>
    /// <param name="accessToken">The canned bearer token (or <see langword="null"/> for an anonymous client).</param>
    /// <param name="baseAddress">The client base address (defaults to <see cref="DefaultBaseAddress"/>).</param>
    public UiHttpServiceHarness(string? accessToken = "test-token", Uri? baseAddress = null)
        : this(new CapturingHttpMessageHandler(), accessToken, baseAddress)
    {
    }

    /// <summary>
    /// Creates the harness in responder-delegate mode: <paramref name="respond"/> answers every
    /// request (invoked once per request so repeated calls get fresh responses).
    /// </summary>
    /// <param name="respond">The responder producing a response per request.</param>
    /// <param name="accessToken">The canned bearer token (or <see langword="null"/> for an anonymous client).</param>
    /// <param name="baseAddress">The client base address (defaults to <see cref="DefaultBaseAddress"/>).</param>
    public UiHttpServiceHarness(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? accessToken = "test-token",
        Uri? baseAddress = null)
        : this(new CapturingHttpMessageHandler(respond), accessToken, baseAddress)
    {
    }

    private UiHttpServiceHarness(CapturingHttpMessageHandler handler, string? accessToken, Uri? baseAddress)
    {
        Handler = handler;
        BaseAddress = baseAddress ?? DefaultBaseAddress;
        ClientFactory = new FreshApiClientFactory(handler, BaseAddress);
        TokenStorage = new StubTokenStorageService(accessToken);
    }

    /// <summary>The shared handler recording every request and answering with the canned responses.</summary>
    public CapturingHttpMessageHandler Handler { get; }

    /// <summary>The base address applied to every created client.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The client factory to hand to the service under test.</summary>
    public IHttpClientFactory ClientFactory { get; }

    /// <summary>The token storage stub to hand to the service under test.</summary>
    public StubTokenStorageService TokenStorage { get; }

    public void Dispose() => Handler.Dispose();
}

/// <summary>
/// <see cref="IHttpClientFactory"/> double producing a fresh <see cref="HttpClient"/> per
/// <see cref="CreateClient"/> call (whatever the requested name, typically <c>"APIClient"</c>),
/// wired to the shared handler with the given base address. A fresh instance per call is
/// load-bearing: the UI services dispose the client after each request, so a factory that caches
/// the instance would hand later calls a disposed client.
/// </summary>
public sealed class FreshApiClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
{
    /// <summary>
    /// Creates a fresh client (never cached) on the shared handler; the handler outlives each client
    /// (<c>disposeHandler: false</c>).
    /// </summary>
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = baseAddress };
}
