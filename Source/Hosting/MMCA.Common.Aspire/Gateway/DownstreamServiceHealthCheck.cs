using System.Globalization;
using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// Probes one downstream service's <c>/alive</c> endpoint through a service-discovery-resolved
/// <see cref="HttpClient"/> and reports the outcome as this host's health.
/// <para>
/// A hand-rolled <see cref="IHealthCheck"/> rather than the <c>AspNetCore.HealthChecks.Uris</c>
/// package: the whole check is one GET and a status-code comparison, and adding a package to a
/// framework assembly that fifteen consumers restore is a real cost for no behaviour a dozen lines
/// do not already give.
/// </para>
/// <para>
/// <b>It probes <c>/alive</c>, not <c>/health/ready</c>, on purpose.</b> Readiness is a downstream
/// replica's own business (its ingress already uses it to route around a warming replica); what the
/// gateway needs to know is whether the service EXISTS and is reachable at all. Probing readiness
/// would make every rolling deployment downstream register as a gateway health failure.
/// </para>
/// <para>
/// Under <see cref="DownstreamProbeVersion.Auto"/> the first probe asks for HTTP/2 and, if the
/// downstream refuses the protocol, retries once as HTTP/1.1 within the same check, so one poll
/// still yields one verdict. The version that answered is latched for the life of this instance,
/// and the health-check service holds one instance per downstream, so the latch is effectively per
/// downstream for the life of the process. That is safe because a service cannot change the
/// protocol of its cleartext endpoint without a redeploy, and a redeploy of the topology restarts
/// this gateway too: a stale latch cannot outlive the endpoint that justified it.
/// </para>
/// </summary>
/// <param name="httpClientFactory">Factory for the named, service-discovery-wired client.</param>
/// <param name="serviceName">The Aspire service name being probed, used for messages.</param>
/// <param name="clientName">The named <see cref="HttpClient"/> registration to resolve.</param>
/// <param name="probeVersion">
/// The version profile chosen at registration. <see cref="DownstreamProbeVersion.Auto"/> negotiates
/// on the first probe; the fixed modes never negotiate.
/// </param>
internal sealed class DownstreamServiceHealthCheck(
    IHttpClientFactory httpClientFactory,
    string serviceName,
    string clientName,
    DownstreamProbeVersion probeVersion) : IHealthCheck
{
    /// <summary>The relative path probed on the downstream service.</summary>
    internal static readonly Uri ProbePath = new(HealthEndpointPaths.Alive, UriKind.Relative);

    /// <summary>
    /// The version <see cref="DownstreamProbeVersion.Auto"/> has settled on, held as the underlying
    /// value of <see cref="DownstreamProbeVersion"/> so that zero, which is
    /// <see cref="DownstreamProbeVersion.Auto"/> itself, reads as "not settled yet". Written with
    /// <c>Interlocked.CompareExchange</c> rather than a plain assignment because two readiness
    /// polls can overlap on a cold gateway: the first writer wins and nothing ever unlatches.
    /// </summary>
    private int _latchedProbeVersion;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var client = httpClientFactory.CreateClient(clientName);

        try
        {
            if (probeVersion == DownstreamProbeVersion.Auto)
            {
                return await NegotiateAsync(client, context, cancellationToken).ConfigureAwait(false);
            }

            return await SendProbeAsync(client, context, probeVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or InvalidOperationException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable, DNS-unresolvable, or slower than the probe timeout. Narrow catch list
            // (rather than a blanket Exception) so a genuine programming error still surfaces, and
            // the cancellation guard keeps host shutdown from being reported as a dependency fault.
            // Nothing latches on this path on purpose: a transient outage says nothing about which
            // protocol the endpoint speaks, and pinning the wrong one would outlive the outage.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Downstream service '" + serviceName + "' did not answer /alive.",
                ex);
        }
    }

    /// <summary>
    /// Runs the <see cref="DownstreamProbeVersion.Auto"/> path: reuse the latched version once one
    /// exists, otherwise ask for HTTP/2 and fall back to HTTP/1.1 on a protocol refusal.
    /// </summary>
    /// <param name="client">The service-discovery-wired probe client.</param>
    /// <param name="context">The health-check context supplying the failure status.</param>
    /// <param name="cancellationToken">The probe's cancellation token.</param>
    /// <returns>The health result for this poll.</returns>
    private async Task<HealthCheckResult> NegotiateAsync(
        HttpClient client,
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        var latched = (DownstreamProbeVersion)Volatile.Read(ref _latchedProbeVersion);
        if (latched != DownstreamProbeVersion.Auto)
        {
            return await SendProbeAsync(client, context, latched, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await SendProbeAsync(client, context, DownstreamProbeVersion.Http2, cancellationToken)
                .ConfigureAwait(false);

            // Any HTTP response, whatever its status, proves the endpoint speaks HTTP/2. The status
            // itself is the health verdict and has nothing to do with the protocol choice.
            Latch(DownstreamProbeVersion.Http2);
            return result;
        }
        catch (HttpRequestException ex) when (IsProtocolRefusal(ex) && !cancellationToken.IsCancellationRequested)
        {
            // The connection was fine and the protocol was not: a cleartext Http1AndHttp2 endpoint
            // without ALPN answers HTTP_1_1_REQUIRED forever. Retry once inside this same check and
            // token budget so the poll still returns a verdict. If HTTP/1.1 also fails, the caller's
            // handler reports Unhealthy and nothing latches.
            var result = await SendProbeAsync(client, context, DownstreamProbeVersion.Http11, cancellationToken)
                .ConfigureAwait(false);

            Latch(DownstreamProbeVersion.Http11);
            return result;
        }
    }

    /// <summary>
    /// Sends one <c>/alive</c> GET at the given version and maps the answer onto a health status.
    /// </summary>
    /// <param name="client">The service-discovery-wired probe client.</param>
    /// <param name="context">The health-check context supplying the failure status.</param>
    /// <param name="version">
    /// The resolved version to send. Callers resolve <see cref="DownstreamProbeVersion.Auto"/> first.
    /// </param>
    /// <param name="cancellationToken">The probe's cancellation token.</param>
    /// <returns>The health result for this attempt.</returns>
    private async Task<HealthCheckResult> SendProbeAsync(
        HttpClient client,
        HealthCheckContext context,
        DownstreamProbeVersion version,
        CancellationToken cancellationToken)
    {
        var http11 = version == DownstreamProbeVersion.Http11;

        // Set per request, not on the client: one client serves both attempts of a negotiation.
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbePath)
        {
            Version = http11 ? HttpVersion.Version11 : HttpVersion.Version20,
            VersionPolicy = http11
                ? HttpVersionPolicy.RequestVersionOrLower
                : HttpVersionPolicy.RequestVersionExact,
        };

        using var response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return HealthCheckResult.Healthy(
                "Downstream service '" + serviceName + "' answered /alive with "
                + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        return new HealthCheckResult(
            context.Registration.FailureStatus,
            "Downstream service '" + serviceName + "' answered /alive with "
            + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>
    /// Whether the failure is the downstream refusing the HTTP VERSION rather than a connectivity
    /// fault. Only these two errors justify retrying at a different version: DNS, a refused
    /// connection, a TLS fault or a timeout say nothing about which protocol the endpoint speaks.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <returns><see langword="true"/> when another HTTP version is worth trying.</returns>
    private static bool IsProtocolRefusal(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.VersionNegotiationError
            or HttpRequestError.HttpProtocolError;

    /// <summary>
    /// Records the version that answered. First writer wins, and a latched value is never replaced.
    /// </summary>
    /// <param name="version">The version to latch.</param>
    private void Latch(DownstreamProbeVersion version) =>
        Interlocked.CompareExchange(
            ref _latchedProbeVersion,
            (int)version,
            (int)DownstreamProbeVersion.Auto);
}
