using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using AwesomeAssertions;
using MMCA.Common.API.RateLimiting;
using static MMCA.Common.Aspire.Hosting.Tests.Support.PublishModeEnvironment;

namespace MMCA.Common.Aspire.Hosting.Tests.Topology;

/// <summary>
/// Unit tests for the multi-replica opt-in: <c>MmcaReplicas.FromEnvironment</c> (every value that is
/// not an integer above 1 reads as the single-instance stack) and <c>WithMmcaReplicas</c> (the
/// replica annotation and the distributed rate-limiting switch it leaves on the model, and that a
/// count of 1 or less leaves the model untouched). Joins the E2E environment collection because
/// <c>FromEnvironment</c> reads a process-global environment variable.
/// </summary>
[Collection(E2eEnvironmentCollection.Name)]
public sealed class MmcaReplicasTests
{
    private const string Variable = "MMCA_HOSTING_TESTS_REPLICAS";

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("abc", 1)]
    [InlineData("2.5", 1)]
    [InlineData("-3", 1)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData(" 3 ", 3)]
    public void FromEnvironment_ReturnsTheCount_OrOneForAnythingElse(string? value, int expected)
    {
        var original = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, value);

            MmcaReplicas.FromEnvironment(Variable).Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, original);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FromEnvironment_BlankVariableName_Throws(string? variableName) =>
        FluentActions.Invoking(() => MmcaReplicas.FromEnvironment(variableName!))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void WithMmcaReplicas_NullBuilder_Throws()
    {
        IResourceBuilder<DotnetProjectResource> nullService = null!;

        FluentActions.Invoking(() => nullService.WithMmcaReplicas(2, distributedRateLimiting: true))
            .Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WithMmcaReplicas_OneOrLess_LeavesTheModelUntouched(int replicas)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = builder.AddResource(new DotnetProjectResource("svc", "."));

        service.WithMmcaReplicas(replicas, distributedRateLimiting: true).Should().BeSameAs(service);

        service.Resource.Annotations.OfType<ReplicaAnnotation>().Should().BeEmpty();
        var environment = await EnvironmentOf(builder, service.Resource);
        environment.Should().NotContainKey("RateLimiting__Distributed");
    }

    [Fact]
    public async Task WithMmcaReplicas_AboveOne_SetsTheReplicaCount_AndDistributedRateLimiting()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = builder.AddResource(new DotnetProjectResource("svc", "."));

        service.WithMmcaReplicas(3, distributedRateLimiting: true).Should().BeSameAs(service);

        service.Resource.Annotations.OfType<ReplicaAnnotation>().Should().ContainSingle()
            .Which.Replicas.Should().Be(3);
        var environment = await EnvironmentOf(builder, service.Resource);
        environment.Should().ContainKey("RateLimiting__Distributed")
            .WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task WithMmcaReplicas_WithoutDistributedRateLimiting_SetsOnlyTheReplicaCount()
    {
        // The gateway's case: its edge limiters stay per replica, as in production.
        var builder = DistributedApplication.CreateBuilder([]);
        var gateway = builder.AddResource(new DotnetProjectResource("gateway", "."));

        gateway.WithMmcaReplicas(4, distributedRateLimiting: false);

        gateway.Resource.Annotations.OfType<ReplicaAnnotation>().Should().ContainSingle()
            .Which.Replicas.Should().Be(4);
        var environment = await EnvironmentOf(builder, gateway.Resource);
        environment.Should().NotContainKey("RateLimiting__Distributed");
    }

    [Fact]
    public async Task WithMmcaReplicas_EmitsTheKeyDerivedFromTheCommonOwnedSettings()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = builder.AddResource(new DotnetProjectResource("svc", "."));

        service.WithMmcaReplicas(2, distributedRateLimiting: true);

        var environment = await EnvironmentOf(builder, service.Resource);
        environment.Should().ContainKey(
            $"{RateLimitingSettings.SectionName}__{nameof(RateLimitingSettings.Distributed)}");
    }
}
