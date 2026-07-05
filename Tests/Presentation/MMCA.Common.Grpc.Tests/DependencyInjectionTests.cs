using AwesomeAssertions;
using Grpc.AspNetCore.Server;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Grpc.Interceptors;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Registration-level assertions for <see cref="DependencyInjection"/>: the typed-client
/// convention must wire the JWT-forwarding interceptor (client-scoped) plus the accessor it
/// depends on and point the client at the service-discovery address, and the server defaults
/// must register the Result-to-RpcException interceptor with detailed errors off. Channel and
/// call behavior is covered elsewhere (<see cref="ResilienceHandlerTests"/>,
/// <see cref="JwtForwardingClientInterceptorTests"/>).
/// </summary>
public sealed class DependencyInjectionTests
{
    private sealed class FakeClient;

    // ── AddTypedGrpcClient: supporting registrations ──
    [Fact]
    public void AddTypedGrpcClient_RegistersJwtForwardingInterceptorAndHttpContextAccessor()
    {
        var services = new ServiceCollection();

        services.AddTypedGrpcClient<FakeClient>("catalog");

        services.Should().Contain(d =>
            d.ServiceType == typeof(JwtForwardingClientInterceptor)
            && d.Lifetime == ServiceLifetime.Transient);
        services.Should().Contain(d => d.ServiceType == typeof(IHttpContextAccessor));
    }

    // ── AddTypedGrpcClient: service-discovery address ──
    [Fact]
    public async Task AddTypedGrpcClient_PointsClientAtHttpServiceDiscoveryAddress()
    {
        var services = new ServiceCollection();
        services.AddTypedGrpcClient<FakeClient>("catalog");
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<GrpcClientFactoryOptions>>()
            .Get(nameof(FakeClient));

        options.Address.Should().Be(
            new Uri("http://catalog"),
            "the address must be the h2c service-discovery form documented in DependencyInjection");
    }

    // ── AddTypedGrpcClient: client-scoped interceptor registration ──
    [Fact]
    public async Task AddTypedGrpcClient_AddsSingleClientScopedInterceptorRegistration()
    {
        var services = new ServiceCollection();
        services.AddTypedGrpcClient<FakeClient>("catalog");
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<GrpcClientFactoryOptions>>()
            .Get(nameof(FakeClient));

        options.InterceptorRegistrations.Should().ContainSingle()
            .Which.Scope.Should().Be(
                InterceptorScope.Client,
                "one interceptor instance per client keeps the forwarding stateless per call chain");
    }

    // ── AddGrpcServiceDefaults: server-side defaults ──
    [Fact]
    public async Task AddGrpcServiceDefaults_RegistersResultInterceptorReflectionAndDisablesDetailedErrors()
    {
        var services = new ServiceCollection();

        services.AddGrpcServiceDefaults();

        services.Should().Contain(d =>
            d.ServiceType == typeof(GrpcResultExceptionInterceptor)
            && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(
            d => d.ServiceType.FullName != null
                && d.ServiceType.FullName.Contains("Reflection", StringComparison.Ordinal),
            "server reflection must be registered so grpcurl-style tooling can introspect the schema");

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;
        options.EnableDetailedErrors.Should().BeFalse("detailed errors must stay off by default");
        options.Interceptors.Should().ContainSingle(
            r => r.Type == typeof(GrpcResultExceptionInterceptor),
            "every gRPC service must translate Result failures via the shared interceptor");
    }
}
