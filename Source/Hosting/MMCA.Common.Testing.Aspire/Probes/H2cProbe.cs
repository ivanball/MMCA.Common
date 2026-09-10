using System.Net;

namespace MMCA.Common.Testing.Aspire.Probes;

/// <summary>
/// Issues a GET over HTTP/2 with prior knowledge (h2c) and refuses to fall back to HTTP/1.1, so a
/// test can assert that a service's cleartext listener really speaks the protocol its gRPC callers
/// need rather than merely answering something.
/// <para>
/// The version pair is the whole point. With no TLS there is no ALPN to negotiate with, so the
/// request has to go out as HTTP/2; a listener that only speaks HTTP/1.1 answers that with a
/// connection error rather than a response, and a listener configured Http2-only answers a default
/// HttpClient's HTTP/1.1 request with GOAWAY <c>HTTP_1_1_REQUIRED</c>. Either way the assertion is
/// only meaningful when the client is pinned to exact HTTP/2, which
/// <see cref="HttpVersionPolicy.RequestVersionExact"/> is.
/// </para>
/// <para>
/// <b>The keep-alive ping trio is load-bearing, not tuning</b> (it is the same trio the framework's
/// AppHost-side h2c health check carries). The client is shared for the process lifetime and HTTP/2
/// pools ONE connection per origin, so a connection that was accepted by a proxy and never answered
/// by the target would otherwise queue every later probe onto a zombie forever. A connection that
/// stops answering pings is torn down instead, and the next probe opens a fresh one.
/// </para>
/// </summary>
public static class H2cProbe
{
    /// <summary>Default per-request budget. Short: a probe slower than this is a failure, not a wait.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly SocketsHttpHandler ProbeHandler = new()
    {
        UseProxy = false,
        KeepAlivePingDelay = TimeSpan.FromSeconds(1),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(1),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
    };

    private static readonly HttpClient ProbeClient =
        new(ProbeHandler, disposeHandler: false) { Timeout = DefaultTimeout };

    /// <summary>
    /// GETs <paramref name="requestUri"/> over cleartext HTTP/2 with prior knowledge.
    /// </summary>
    /// <param name="requestUri">The absolute URI to GET.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public static async Task<HttpResponseMessage> SendAsync(Uri requestUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        using var request = CreateRequest(requestUri);
        return await ProbeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the pinned request. Exposed so a test can assert the version pair itself, which is the
    /// only part of this type worth asserting without a server.
    /// </summary>
    /// <param name="requestUri">The absolute URI to GET.</param>
    /// <returns>A GET pinned to exact HTTP/2.</returns>
    public static HttpRequestMessage CreateRequest(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        return new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }
}
