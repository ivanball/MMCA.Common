using System.Globalization;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace MMCA.Common.Testing;

/// <summary>
/// An <see cref="IHttpForwarder"/> that never proxies: it echoes the destination prefix, the matched
/// cluster, and the <see cref="ForwarderRequestConfig"/> it was handed into response headers, so a
/// gateway test can assert which cluster a route targets and which forwarder budget it carries.
/// Echoing into response headers rather than storing state keeps the singleton fake safe for
/// concurrent requests.
/// <para>
/// Register it by replacing the real singleton in the test service collection
/// (<c>services.Replace(ServiceDescriptor.Singleton&lt;IHttpForwarder&gt;(new RecordingHttpForwarder()))</c>).
/// That still intercepts under a config-driven route table: YARP's own forwarder middleware resolves
/// <see cref="IHttpForwarder"/> from DI, so <c>MapReverseProxy()</c> goes through this fake exactly as
/// a hand-written <c>MapForwarder</c> call would. It also keeps every gateway test off the network,
/// since configured destinations are usually service-discovery names that resolve to nothing in a
/// test host.
/// </para>
/// <para>
/// The REAL transform pipeline is run against a throwaway outbound request before the echo, so the
/// request transforms (the gateway's route/cluster trace headers among them) are observable without a
/// network hop, exactly as the real forwarder would produce them.
/// </para>
/// </summary>
public sealed class RecordingHttpForwarder : IHttpForwarder
{
    /// <summary>Header the fake writes the destination prefix into.</summary>
    public const string DestinationHeader = "X-Test-Forward-Destination";

    /// <summary>Header the fake writes the matched cluster id into.</summary>
    public const string ClusterHeader = "X-Test-Forward-Cluster";

    /// <summary>Header the fake writes the forwarder's activity timeout into.</summary>
    public const string ActivityTimeoutHeader = "X-Test-Forward-Activity-Timeout";

    /// <summary>Header the fake writes the forwarded HTTP version into.</summary>
    public const string VersionHeader = "X-Test-Forward-Version";

    /// <summary>Header the fake writes the forwarded HTTP version policy into.</summary>
    public const string VersionPolicyHeader = "X-Test-Forward-Version-Policy";

    /// <summary>
    /// Header the fake writes the value of the route trace header the transform pipeline stamped on
    /// the OUTBOUND request into. The real transforms run to produce it, so this observes what a
    /// downstream service would actually receive.
    /// </summary>
    public const string RouteTraceEchoHeader = "X-Test-Forward-Route-Trace";

    /// <summary>Header the fake writes the stamped cluster trace header into. See <see cref="RouteTraceEchoHeader"/>.</summary>
    public const string ClusterTraceEchoHeader = "X-Test-Forward-Cluster-Trace";

    /// <summary>Value echoed when the forwarder config leaves a nullable setting unset.</summary>
    public const string UnsetValue = "(unset)";

    /// <summary>
    /// The OUTBOUND route trace header the gateway's transform pipeline stamps, read back into
    /// <see cref="RouteTraceEchoHeader"/>. Defaults to the shared gateway kit's own header name.
    /// </summary>
    public string RouteTraceHeaderName { get; init; } = "X-MMCA-Route";

    /// <summary>
    /// The OUTBOUND cluster trace header the gateway's transform pipeline stamps, read back into
    /// <see cref="ClusterTraceEchoHeader"/>. Defaults to the shared gateway kit's own header name.
    /// </summary>
    public string ClusterTraceHeaderName { get; init; } = "X-MMCA-Cluster";

    /// <inheritdoc />
    public ValueTask<ForwarderError> SendAsync(
        HttpContext context,
        string destinationPrefix,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        HttpTransformer transformer) =>
        SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer, CancellationToken.None);

    /// <inheritdoc />
    public async ValueTask<ForwarderError> SendAsync(
        HttpContext context,
        string destinationPrefix,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        HttpTransformer transformer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestConfig);
        ArgumentNullException.ThrowIfNull(transformer);

        context.Response.Headers[DestinationHeader] = destinationPrefix;
        context.Response.Headers[ClusterHeader] =
            context.Features.Get<IReverseProxyFeature>()?.Route.Config.ClusterId ?? UnsetValue;
        context.Response.Headers[ActivityTimeoutHeader] =
            requestConfig.ActivityTimeout?.ToString("c", CultureInfo.InvariantCulture) ?? UnsetValue;
        context.Response.Headers[VersionHeader] =
            requestConfig.Version?.ToString() ?? UnsetValue;
        context.Response.Headers[VersionPolicyHeader] =
            requestConfig.VersionPolicy?.ToString() ?? UnsetValue;

        // Run the REAL transform pipeline against a throwaway outbound request so the request
        // transforms (the route/cluster trace headers among them) are observable without a network
        // hop. The real forwarder does exactly this before sending.
        using var proxyRequest = new HttpRequestMessage();
        await transformer.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers[RouteTraceEchoHeader] = HeaderOrUnset(proxyRequest, RouteTraceHeaderName);
        context.Response.Headers[ClusterTraceEchoHeader] = HeaderOrUnset(proxyRequest, ClusterTraceHeaderName);

        context.Response.StatusCode = StatusCodes.Status200OK;
        return ForwarderError.None;
    }

    /// <summary>Reads one outbound header, or <see cref="UnsetValue"/> when it was never stamped.</summary>
    /// <param name="proxyRequest">The transformed outbound request.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The single header value, or the unset marker.</returns>
    private static string HeaderOrUnset(HttpRequestMessage proxyRequest, string name) =>
        proxyRequest.Headers.TryGetValues(name, out var values)
            ? string.Join(',', values)
            : UnsetValue;
}
