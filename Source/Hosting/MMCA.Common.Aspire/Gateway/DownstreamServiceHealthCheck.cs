using System.Globalization;
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
/// </summary>
/// <param name="httpClientFactory">Factory for the named, service-discovery-wired client.</param>
/// <param name="serviceName">The Aspire service name being probed, used for messages.</param>
/// <param name="clientName">The named <see cref="HttpClient"/> registration to resolve.</param>
internal sealed class DownstreamServiceHealthCheck(
    IHttpClientFactory httpClientFactory,
    string serviceName,
    string clientName) : IHealthCheck
{
    /// <summary>The relative path probed on the downstream service.</summary>
    internal static readonly Uri ProbePath = new("/alive", UriKind.Relative);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var client = httpClientFactory.CreateClient(clientName);

        try
        {
            using var response = await client
                .GetAsync(ProbePath, cancellationToken)
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or InvalidOperationException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable, DNS-unresolvable, or slower than the probe timeout. Narrow catch list
            // (rather than a blanket Exception) so a genuine programming error still surfaces, and
            // the cancellation guard keeps host shutdown from being reported as a dependency fault.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Downstream service '" + serviceName + "' did not answer /alive.",
                ex);
        }
    }
}
