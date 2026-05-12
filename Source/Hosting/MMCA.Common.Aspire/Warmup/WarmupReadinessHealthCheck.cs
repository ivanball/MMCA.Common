using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// Health check that reports unhealthy until the <see cref="WarmupReadinessGate"/> opens. Tagged
/// <c>ready</c> so it appears in the <c>/health/ready</c> endpoint that ACA readiness probes hit.
/// </summary>
internal sealed class WarmupReadinessHealthCheck(WarmupReadinessGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Warm-up complete.")
            : HealthCheckResult.Unhealthy("Warm-up in progress."));
}
