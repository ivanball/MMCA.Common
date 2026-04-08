using MMCA.Common.Application.Messaging;
using MMCA.Common.Architecture.Tests.Helpers;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Architecture rules added in support of the modular-monolith → microservices extraction.
/// These rules pin the layering boundaries that the extraction depends on, so that future
/// changes can't accidentally couple the framework to a specific transport implementation
/// or leak transport types into application code.
/// </summary>
public sealed class MicroserviceExtractionTests
{
    [Fact]
    public void Application_ShouldNotDependOn_MassTransit() =>
        AssertNoDependency(
            PackageAssemblies.Application,
            "MassTransit",
            "MMCA.Common.Application must not reference MassTransit types directly — depend on IMessageBus instead");

    [Fact]
    public void Domain_ShouldNotDependOn_MassTransit() =>
        AssertNoDependency(
            PackageAssemblies.Domain,
            "MassTransit",
            "MMCA.Common.Domain must remain transport-agnostic");

    [Fact]
    public void Shared_ShouldNotDependOn_MassTransit() =>
        AssertNoDependency(
            PackageAssemblies.Shared,
            "MassTransit",
            "MMCA.Common.Shared is the foundation layer; transport packages do not belong here");

    [Fact]
    public void Grpc_ShouldNotDependOn_Domain() =>
        AssertNoDependency(
            PackageAssemblies.Grpc,
            "MMCA.Common.Domain",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Domain");

    [Fact]
    public void Grpc_ShouldNotDependOn_Application() =>
        AssertNoDependency(
            PackageAssemblies.Grpc,
            "MMCA.Common.Application",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Application");

    [Fact]
    public void Grpc_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(
            PackageAssemblies.Grpc,
            "MMCA.Common.Infrastructure",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Infrastructure");

    [Fact]
    public void IMessageBus_LivesInApplicationLayer()
    {
        // Sanity check: the abstraction must remain in Application so that consuming
        // services depend on it through Application, not through Infrastructure.
        var assembly = typeof(IMessageBus).Assembly;
        assembly.Should().BeSameAs(PackageAssemblies.Application);
    }

    [Fact]
    public void IJwksProvider_LivesInInfrastructureLayer()
    {
        // The provider implementation deals with crypto/PEM material, so it lives in
        // Infrastructure. The /.well-known/jwks.json endpoint in API consumes it via DI.
        var assembly = typeof(IJwksProvider).Assembly;
        assembly.Should().BeSameAs(PackageAssemblies.Infrastructure);
    }

    private static void AssertNoDependency(Assembly assembly, string forbiddenNamespace, string reason)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespace)
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result, reason);
    }
}
