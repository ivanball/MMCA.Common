using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// Composes the two shipped host extensions the way a real <c>Program.cs</c> does:
/// <c>AddServiceDefaults()</c> (service discovery plus the Polly resilience pipeline, applied to
/// every client through <c>ConfigureHttpClientDefaults</c>) and then
/// <c>AddTrustedCallerHeader(configuration)</c>.
/// <para>
/// <b>What this pins.</b> Both extensions compose <see cref="System.Net.Http.DelegatingHandler"/>s
/// onto the same pipeline, and the trusted-caller gate compares the request's authority against the
/// configured gateway origin. Service discovery REWRITES that authority mid-pipeline: a host whose
/// <c>Api:ApiEndpoint</c> is an Aspire discovery name (<c>https+http://gateway</c>) sends a request
/// that starts with that authority and reaches the socket with the resolved one. Whether the header
/// is stamped therefore depends on where in the handler chain the gate sits, which is not something
/// a host configures or can see. The registration pins the trusted-caller handler OUTERMOST, so the
/// gate always judges the authority the caller wrote, which is the authority the host configured.
/// </para>
/// <para>
/// Neither MMCA.ADC nor MMCA.Store configures a discovery name today (both point
/// <c>Api:ApiEndpoint</c> at an absolute URL), so this is a latent trap rather than a regression:
/// the first host to adopt Aspire-style discovery for its gateway would have silently lost the
/// rate-limit exemption, and the only symptom would be the gateway throttling the application under
/// load.
/// </para>
/// </summary>
public sealed class TrustedCallerServiceDiscoveryTests
{
    private const string DefaultHeaderName = "X-Internal-Caller-Key";
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string DiscoveryOrigin = "https+http://gateway";
    private const string ResolvedHost = "gateway.local";

    [Fact]
    public async Task TrustedCallerHeader_SurvivesServiceDiscoveryRewriting_TheGatewayAuthority()
    {
        var sent = await SendThroughDiscoveredClientAsync(DiscoveryOrigin + "/Auth/refresh");

        sent.Uri!.Host.Should().Be(
            ResolvedHost,
            "the test is only meaningful if service discovery actually rewrote the authority");
        sent.CallerSecret.Should().Be(
            Secret,
            "the gateway origin the host configured IS the discovery name, so the exemption must "
            + "apply to the call that discovery resolves from it");
    }

    [Fact]
    public async Task TrustedCallerHeader_StillStampsNothingBoundElsewhere_UnderServiceDiscovery()
    {
        var sent = await SendThroughDiscoveredClientAsync("https://telemetry.example.com/ingest");

        sent.CallerSecret.Should().BeNull(
            "the origin gate is the whole reason breadth is safe: a client pointed at a third party "
            + "must never be handed the bypass");
    }

    /// <summary>
    /// Builds a host that registers BOTH extensions, resolves a named client from the factory, and
    /// returns what reached the socket.
    /// </summary>
    /// <remarks>
    /// The URI and the header are read INSIDE the primary handler, not off the request object
    /// afterwards: the service-discovery handler restores the original URI once the send completes,
    /// so a request inspected after the fact still shows the unresolved authority and would make
    /// this test pass vacuously.
    /// </remarks>
    /// <param name="requestUri">The URI the caller asks for.</param>
    /// <returns>What the socket saw.</returns>
    private static async Task<SentRequest> SendThroughDiscoveredClientAsync(string requestUri)
    {
        var inner = new CapturingHandler();
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The gateway origin is a discovery name, not an absolute URL.
            ["Api:ApiEndpoint"] = DiscoveryOrigin,
            ["GatewayRateLimiting:TrustedCallerSecret"] = Secret,

            // The configuration-backed endpoint provider AddServiceDiscovery registers by default:
            // this is the stub resolver, expressed the way a real deployment expresses it.
            ["Services:gateway:https:0"] = ResolvedHost + ":443",
        });

        // Registration order as a real Program.cs writes it: defaults first, host extensions after.
        builder.AddServiceDefaults();
        builder.Services.AddTrustedCallerHeader(builder.Configuration);
        builder.Services.AddHttpClient("Gateway").ConfigurePrimaryHttpMessageHandler(() => inner);

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Gateway");

        await client.GetAsync(new Uri(requestUri), TestContext.Current.CancellationToken);

        inner.Sent.Should().NotBeNull();
        return inner.Sent;
    }

    /// <summary>What the primary handler observed, snapshotted at send time.</summary>
    /// <param name="Uri">The request URI as the socket saw it.</param>
    /// <param name="CallerSecret">The trusted-caller header value, or null when it was not stamped.</param>
    private sealed record SentRequest(Uri? Uri, string? CallerSecret);

    /// <summary>Stub primary handler that snapshots the request the client pipeline produced.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public SentRequest? Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Sent = new SentRequest(
                request.RequestUri,
                request.Headers.TryGetValues(DefaultHeaderName, out var values)
                    ? values.SingleOrDefault()
                    : null);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
