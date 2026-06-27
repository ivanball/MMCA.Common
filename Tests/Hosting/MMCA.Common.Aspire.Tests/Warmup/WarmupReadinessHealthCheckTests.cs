using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire.Tests.Warmup;

/// <summary>
/// The warm-up readiness health check (rubric §29/§13): reports Unhealthy while warming so
/// <c>/health/ready</c> keeps a cold replica out of rotation, then Healthy once the gate opens.
/// </summary>
public sealed class WarmupReadinessHealthCheckTests
{
    private static async Task<HealthStatus> CheckAsync(WarmupReadinessGate gate)
    {
        var check = new WarmupReadinessHealthCheck(gate);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        return result.Status;
    }

    [Fact]
    public async Task BeforeGateOpens_ReportsUnhealthy() =>
        (await CheckAsync(new WarmupReadinessGate())).Should().Be(HealthStatus.Unhealthy);

    [Fact]
    public async Task AfterGateOpens_ReportsHealthy()
    {
        var gate = new WarmupReadinessGate();
        gate.MarkReady();

        (await CheckAsync(gate)).Should().Be(HealthStatus.Healthy);
    }
}
