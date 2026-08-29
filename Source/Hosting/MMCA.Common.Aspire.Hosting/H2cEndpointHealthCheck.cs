using System.Globalization;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// GETs one path on an Aspire endpoint over HTTP/2 with prior knowledge (h2c) and reports the
/// outcome as that resource's health.
/// <para>
/// A hand-rolled <see cref="IHealthCheck"/> rather than Aspire's stock HTTP health check: the stock
/// one probes with a default <see cref="HttpClient"/>, so the request goes out HTTP/1.1 and an
/// Http2-only cleartext endpoint refuses it with GOAWAY <c>HTTP_1_1_REQUIRED</c>. The version pair
/// below is the whole difference, and it is the same pair the framework's gateway probes use.
/// </para>
/// <para>
/// The endpoint URL is resolved on every check, not captured at registration time: the AppHost
/// registers this check while building the application model, and Aspire only allocates the endpoint
/// once the resource starts.
/// </para>
/// </summary>
internal sealed class H2cEndpointHealthCheck : IHealthCheck
{
    /// <summary>
    /// Proxy detection is off: the probe target is a sibling process on the developer's own machine
    /// (or a sibling replica inside the deployment network), never something a proxy should see.
    /// </summary>
    private static readonly SocketsHttpHandler ProbeHandler = new() { UseProxy = false };

    /// <summary>
    /// One process-lifetime client for every probe. Static rather than per-registration so a handful
    /// of gated resources do not each hold their own connection pool, and so this type owns no
    /// disposable instance state.
    /// </summary>
    private static readonly HttpClient ProbeClient =
        new(ProbeHandler, disposeHandler: false) { Timeout = H2cHealthCheckExtensions.ProbeTimeout };

    private readonly Func<string> _resolveEndpointUrl;
    private readonly string _path;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>
    /// Creates the probe used by <c>WithH2cHealthCheck</c>.
    /// </summary>
    /// <param name="endpoint">The resource endpoint to probe.</param>
    /// <param name="path">The path to GET on that endpoint.</param>
    internal H2cEndpointHealthCheck(EndpointReference endpoint, string path)
        : this(() => endpoint.Url, path, ProbeClient.SendAsync)
    {
    }

    /// <summary>
    /// Test constructor: takes the URL resolver and the send delegate directly, so the probe can be
    /// exercised against a stub handler and against an endpoint that is not yet allocated.
    /// <para>
    /// A send delegate rather than an <see cref="HttpMessageHandler"/> keeps this type free of a
    /// disposable instance field: the shared client above is process-lifetime state, and a
    /// per-instance client would make every health-check registration an owner of one.
    /// </para>
    /// </summary>
    /// <param name="resolveEndpointUrl">Resolves the endpoint base URL, or throws while unallocated.</param>
    /// <param name="path">The path to GET on that endpoint.</param>
    /// <param name="send">Sends the probe request.</param>
    internal H2cEndpointHealthCheck(
        Func<string> resolveEndpointUrl,
        string path,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _resolveEndpointUrl = resolveEndpointUrl;
        _path = path;
        _send = send;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string endpointUrl;
        try
        {
            endpointUrl = _resolveEndpointUrl();
        }
        catch (InvalidOperationException ex)
        {
            // The endpoint has no allocation yet, which is the normal state for the first polls after
            // the AppHost starts. Reporting the failure status (rather than Healthy, and rather than
            // letting the exception escape) is what keeps a WaitFor edge closed: Aspire releases the
            // wait only on Healthy, so a pre-allocation poll must not pass.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Endpoint is not allocated yet, so there is nothing to probe.",
                ex);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(endpointUrl, UriKind.Absolute), _path))
            {
                // h2c prior knowledge: with no TLS there is no ALPN to negotiate with, so the request
                // has to go out as HTTP/2 and must not be allowed to fall back to HTTP/1.1, which an
                // Http2-only Kestrel endpoint answers with GOAWAY HTTP_1_1_REQUIRED.
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            using var response = await _send(request, cancellationToken).ConfigureAwait(false);

            var description = "Endpoint answered " + _path + " with "
                + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " over HTTP/2.";

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy(description);
            }

            return new HealthCheckResult(context.Registration.FailureStatus, description);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or InvalidOperationException or UriFormatException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable, still starting, refusing HTTP/2, or slower than the probe budget. Narrow
            // catch list (rather than a blanket Exception) so a genuine programming error still
            // surfaces, and the cancellation guard keeps AppHost shutdown from being reported as a
            // service fault.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Endpoint did not answer " + _path + " over HTTP/2.",
                ex);
        }
    }
}
