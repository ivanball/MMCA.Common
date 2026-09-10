using AwesomeAssertions;
using MMCA.Common.API.Startup.Endpoints;
using MMCA.Common.Testing.Aspire.Probes;

namespace MMCA.Common.Testing.Aspire.Tests.Probes;

/// <summary>
/// Cross-asserts every mirrored probe path against the framework constant it mirrors.
/// <para>
/// <see cref="AppHostProbePaths"/> restates these strings so an AppHost-tier test package does not
/// have to reference the service-defaults graph or the API layer to spell four paths. That trade is
/// only safe with this test: without it, renaming a probe route would leave the mirror pointing at a
/// path nothing serves, and every consumer's smoke tier would fail on a 404 that looks like a broken
/// service. The same posture MMCA.Common.Aspire.Hosting takes for the gateway configuration sections
/// it mirrors.
/// </para>
/// </summary>
public sealed class AppHostProbePathsTests
{
    [Fact]
    public void Health_MirrorsTheMappedRoute() =>
        AppHostProbePaths.Health.Should().Be(Common.Aspire.HealthEndpointPaths.Health);

    [Fact]
    public void Alive_MirrorsTheMappedRoute() =>
        AppHostProbePaths.Alive.Should().Be(Common.Aspire.HealthEndpointPaths.Alive);

    [Fact]
    public void Ready_MirrorsTheMappedRoute() =>
        AppHostProbePaths.Ready.Should().Be(Common.Aspire.HealthEndpointPaths.Ready);

    [Fact]
    public void Jwks_MirrorsTheMappedRoute() =>
        AppHostProbePaths.Jwks.Should().Be(JwksEndpointExtensions.DefaultJwksPath);

    // The host's own probe classifier drives telemetry filtering and the rate-limit bypass. A path
    // this package probes that the host does not classify as a probe would be throttled like
    // ordinary traffic under an E2E-volume run.
    [Theory]
    [InlineData(AppHostProbePaths.Health)]
    [InlineData(AppHostProbePaths.Alive)]
    [InlineData(AppHostProbePaths.Ready)]
    public void EveryProbePath_IsRecognizedAsAProbePathByTheHost(string path) =>
        Common.Aspire.HealthEndpointPaths.IsProbePath(path).Should().BeTrue();

    [Fact]
    public void CleartextEndpointName_IsTheAspireHttpEndpoint() =>
        AppHostProbePaths.CleartextEndpointName.Should().Be(
            "http",
            "the h2c assertions target the cleartext listener, which Aspire names http");
}
