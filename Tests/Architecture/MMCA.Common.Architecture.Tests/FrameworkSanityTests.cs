using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Framework-specific transport/placement sanity that the shared rule library does not generalize:
/// MMCA.Common is the only repo that owns the <c>MMCA.Common.Grpc</c> transport package and defines the
/// <c>IMessageBus</c> / <c>IJwksProvider</c> abstractions, so these anchors are checked here directly.
/// </summary>
public sealed class FrameworkSanityTests
{
    private static Assembly Grpc => typeof(Common.Grpc.ResultGrpcExtensions).Assembly;

    private static Assembly Application => typeof(Common.Application.Services.DomainEventDispatcher).Assembly;

    private static Assembly Infrastructure => typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly;

    [Fact]
    public void Grpc_ShouldNotDependOn_Domain() =>
        AssertNoDependency(Grpc, "MMCA.Common.Domain",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Domain");

    [Fact]
    public void Grpc_ShouldNotDependOn_Application() =>
        AssertNoDependency(Grpc, "MMCA.Common.Application",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Application");

    [Fact]
    public void Grpc_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(Grpc, "MMCA.Common.Infrastructure",
            "MMCA.Common.Grpc is transport infrastructure — it must not couple to Infrastructure");

    [Fact]
    public void IMessageBus_LivesInApplicationLayer() =>
        typeof(IMessageBus).Assembly.Should().BeSameAs(Application,
            "the message-bus abstraction must remain in Application so consumers depend on it through Application");

    [Fact]
    public void IJwksProvider_LivesInInfrastructureLayer() =>
        typeof(IJwksProvider).Assembly.Should().BeSameAs(Infrastructure,
            "the JWKS provider handles crypto/PEM material, so it belongs in Infrastructure");

    [Fact]
    public void ILiveChannelPublisher_LivesInApplicationLayer() =>
        typeof(ILiveChannelPublisher).Assembly.Should().BeSameAs(Application,
            "the live-channel publish abstraction must remain in Application (beside IPushNotificationSender) so application code stays transport-free");

    private static void AssertNoDependency(Assembly assembly, string forbiddenNamespace, string reason)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespace)
            .GetResult();

        ArchitectureAssert.NoViolations(result, reason);
    }
}
