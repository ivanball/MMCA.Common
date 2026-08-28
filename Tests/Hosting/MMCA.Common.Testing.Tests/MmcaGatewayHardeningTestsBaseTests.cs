using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Guards the shape of <see cref="MmcaGatewayHardeningTestsBase{TEntryPoint}"/>. The gates themselves
/// need a booted gateway host (a consumer's sealed subclass over its own <c>Program</c>), so they are
/// not runnable here; what IS checkable headlessly is that the base still DECLARES every gate and
/// still exposes the extension points a subclass supplies. A gate silently dropped from the base
/// would otherwise stop running in three repos with nothing failing.
/// <para>
/// <see cref="SampleGatewayHardeningTests"/> below is compile coverage: it is abstract (so xUnit never
/// collects it) and exists to prove the extension points are reachable and correctly typed from a
/// subclass, which is the only part of the base a consumer interacts with.
/// </para>
/// </summary>
public sealed class MmcaGatewayHardeningTestsBaseTests
{
    private static readonly string[] ExpectedGates =
    [
        "Route_IsThrottledOnceTheClientExhaustsItsWindow",
        "NamedPolicyRoute_IsThrottledAtItsOwnTighterAllowance",
        "BypassedPaths_AreNotThrottledEvenAfterTheWindowIsExhausted",
        "Response_CarriesAGeneratedCorrelationId_WhenTheCallerSuppliesNone",
        "Response_EchoesTheCallerSuppliedCorrelationId",
        "Readiness_IncludesADownstreamCheckPerService",
        "EveryCluster_CarriesAnActiveHealthCheckProbingAlive",
        "RateLimiter_PartitionsByForwardedClientIp_NotByProxyIp",
    ];

    private static readonly string[] ExpectedAbstractMembers =
    [
        "Factory",
        "PermitLimit",
        "LimitedPath",
        "DownstreamServices",
    ];

    [Fact]
    public void Base_DeclaresEveryEdgeHardeningGate_AsAFact()
    {
        var facts = typeof(MmcaGatewayHardeningTestsBase<>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name);

        facts.Should().BeEquivalentTo(
            ExpectedGates,
            "every gate the base ships runs in each consumer's sealed subclass; dropping or renaming one silently stops it running everywhere");
    }

    [Fact]
    public void Base_RequiresOnlyTheRouteTableFactsFromASubclass()
    {
        var abstractMembers = typeof(MmcaGatewayHardeningTestsBase<>)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetMethod is { IsAbstract: true })
            .Select(p => p.Name);

        // Everything else (bypass list, named policy, probe cadence, header names) is virtual with a
        // framework default, so a host that matches the kit defaults writes four members and nothing more.
        abstractMembers.Should().BeEquivalentTo(ExpectedAbstractMembers);
    }

}

/// <summary>
/// Compile coverage for a consumer subclass. Abstract on purpose so xUnit does not collect it:
/// running these gates needs a real gateway host, which this project does not boot.
/// </summary>
internal abstract class SampleGatewayHardeningTests : MmcaGatewayHardeningTestsBase<SampleGatewayEntryPoint>
{
    protected override WebApplicationFactory<SampleGatewayEntryPoint> Factory =>
        throw new NotSupportedException("Compile coverage only.");

    protected override int PermitLimit => 120;

    protected override string LimitedPath => "/Events/1";

    protected override IReadOnlyList<string> DownstreamServices => ["identity", "conference"];

    protected override IReadOnlyList<string> BypassedPaths =>
        ["/hubs/notifications", "/.well-known/jwks.json", "/health"];

    protected override string? NamedPolicyPath => "/Auth/login";

    protected override int NamedPolicyPermitLimit => 30;

    protected override TimeSpan ActiveProbeInterval => TimeSpan.FromSeconds(30);
}

/// <summary>Stands in for a gateway host's <c>Program</c> in the compile-coverage subclass.</summary>
internal sealed class SampleGatewayEntryPoint;
