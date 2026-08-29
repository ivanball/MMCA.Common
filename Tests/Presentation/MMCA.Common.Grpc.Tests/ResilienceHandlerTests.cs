using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Resilience;
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

    // ── The configured pipeline must carry the shared east-west values, breaker included ──
    [Fact]
    public void AddTypedGrpcClient_ConfiguresTheGrpcResilienceDefaults()
    {
        var services = new ServiceCollection();

        services.AddTypedGrpcClient<FakeGrpcClient>("fake-service");

        // The standard resilience handler names its options "{clientName}-standard", where the
        // client name defaults to the client type's name. Resolving through the options monitor
        // exercises the exact path the handler itself uses, factory registrations included. A
        // wrong name here would come back as library defaults and fail every value assertion.
        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{nameof(FakeGrpcClient)}-standard");

        options.AttemptTimeout.Timeout.Should().Be(GrpcResilienceDefaults.AttemptTimeout);
        options.TotalRequestTimeout.Timeout.Should().Be(GrpcResilienceDefaults.TotalRequestTimeout);
        options.Retry.MaxRetryAttempts.Should().Be(GrpcResilienceDefaults.MaxRetryAttempts);
        options.CircuitBreaker.SamplingDuration.Should().Be(GrpcResilienceDefaults.SamplingDuration);
        options.CircuitBreaker.FailureRatio.Should().Be(
            GrpcResilienceDefaults.FailureRatio,
            "east-west gRPC calls bypass the Gateway's active health checks, so the breaker shape is explicit rather than left at the library default");
        options.CircuitBreaker.MinimumThroughput.Should().Be(GrpcResilienceDefaults.MinimumThroughput);
        options.CircuitBreaker.BreakDuration.Should().Be(GrpcResilienceDefaults.BreakDuration);
    }

    // ── The values themselves, pinned so a silent edit shows up as a failing test ──
    [Fact]
    public void GrpcResilienceDefaults_PinTheEastWestValues()
    {
        GrpcResilienceDefaults.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(30));
        GrpcResilienceDefaults.TotalRequestTimeout.Should().Be(TimeSpan.FromSeconds(90));
        GrpcResilienceDefaults.SamplingDuration.Should().Be(TimeSpan.FromSeconds(60));
        GrpcResilienceDefaults.MaxRetryAttempts.Should().Be(1);
        GrpcResilienceDefaults.FailureRatio.Should().Be(0.5);
        GrpcResilienceDefaults.MinimumThroughput.Should().Be(10);
        GrpcResilienceDefaults.BreakDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    // ── Timeouts and retries stay tied to the outbound-HTTP path, so the two cannot drift ──
    [Fact]
    public void GrpcResilienceDefaults_ShareTheHttpTimeoutAndRetryBudget()
    {
        GrpcResilienceDefaults.AttemptTimeout.Should().Be(HttpResilienceDefaults.AttemptTimeout);
        GrpcResilienceDefaults.TotalRequestTimeout.Should().Be(HttpResilienceDefaults.TotalRequestTimeout);
        GrpcResilienceDefaults.SamplingDuration.Should().Be(HttpResilienceDefaults.CircuitBreakerSamplingDuration);
        GrpcResilienceDefaults.MaxRetryAttempts.Should().Be(HttpResilienceDefaults.MaxRetryAttempts);
    }
}
