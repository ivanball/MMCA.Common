using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Fitness function for ADR-009: every typed gRPC client registered through the framework's
/// convention must wire the standard resilience handler (timeout / retry / circuit breaker), so
/// the policy cannot silently regress when a new outbound client is added.
/// </summary>
public sealed class ResilienceHandlerTests
{
    // A stand-in for a generated gRPC client class. AddTypedGrpcClient only configures
    // registrations (it is never resolved here), so any reference type suffices.
    private sealed class FakeGrpcClient;

    [Fact]
    public void AddTypedGrpcClient_WiresStandardResilienceHandler()
    {
        var services = new ServiceCollection();

        services.AddTypedGrpcClient<FakeGrpcClient>("fake-service");

        // AddStandardResilienceHandler registers options typed as HttpStandardResilienceOptions
        // (configure/validate). Their presence is the registration-level proof that the standard
        // resilience pipeline was applied to this client; removing the handler removes them.
        services.Should().Contain(
            descriptor => descriptor.ServiceType.FullName != null
                && descriptor.ServiceType.FullName.Contains(nameof(HttpStandardResilienceOptions), StringComparison.Ordinal),
            "every typed gRPC client must wire the standard resilience handler (ADR-009)");
    }

    [Fact]
    public void AddTypedGrpcClient_RequiresServiceName()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTypedGrpcClient<FakeGrpcClient>("  ");

        act.Should().Throw<ArgumentException>();
    }
}
