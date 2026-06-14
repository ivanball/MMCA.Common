using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using MMCA.Common.Grpc;
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

        // AddStandardResilienceHandler configures named HttpStandardResilienceOptions; its presence
        // is the registration-level proof that the resilience pipeline was applied to this client.
        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<HttpStandardResilienceOptions>),
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
