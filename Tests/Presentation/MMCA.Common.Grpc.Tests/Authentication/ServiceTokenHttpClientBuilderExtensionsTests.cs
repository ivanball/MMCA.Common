using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Grpc.Authentication;
using MMCA.Common.Grpc.Interceptors;
using Moq;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Registration-level assertions for <c>WithServiceToken()</c>: chained onto
/// <c>AddTypedGrpcClient&lt;TClient&gt;</c> it adds <see cref="ServiceTokenClientInterceptor"/>
/// (client-scoped) to that client's registration alone, so a sibling typed client that did not opt in
/// keeps only the JWT-forwarding interceptor; and a host that opts a client in without calling
/// <c>AddServiceTokenClient</c> fails when that client is created, naming the missing
/// <see cref="IServiceTokenProvider"/>, rather than silently sending no token.
/// </summary>
public sealed class ServiceTokenHttpClientBuilderExtensionsTests
{
    // -- The interceptor goes to the opted-in client only --
    [Fact]
    public async Task WithServiceToken_AddsTheServiceTokenInterceptorToThatClientOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IServiceTokenProvider>());
        services.AddTypedGrpcClient<OptedInClient>("catalog").WithServiceToken();
        services.AddTypedGrpcClient<SiblingClient>("ordering");
        await using var provider = services.BuildServiceProvider();
        var factoryOptions = provider.GetRequiredService<IOptionsMonitor<GrpcClientFactoryOptions>>();

        var optedIn = factoryOptions.Get(nameof(OptedInClient)).InterceptorRegistrations;
        var sibling = factoryOptions.Get(nameof(SiblingClient)).InterceptorRegistrations;

        optedIn.Select(r => r.Creator(provider).GetType()).Should().BeEquivalentTo(
            [typeof(JwtForwardingClientInterceptor), typeof(ServiceTokenClientInterceptor)],
            "the opted-in client forwards the user's token and falls back to the service token");
        optedIn.Should().OnlyContain(r => r.Scope == InterceptorScope.Client);
        sibling.Select(r => r.Creator(provider).GetType()).Should().Equal(
            [typeof(JwtForwardingClientInterceptor)],
            "a client that did not opt in must never send a service token its server may not validate");
    }

    // -- Opting in without a provider fails loudly --
    [Fact]
    public async Task WithServiceToken_WithoutAddServiceTokenClient_FailsToCreateTheClientNamingTheProvider()
    {
        var services = new ServiceCollection();
        services.AddTypedGrpcClient<OptedInClient>("catalog").WithServiceToken();
        services.AddTypedGrpcClient<SiblingClient>("ordering");
        await using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<OptedInClient>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(IServiceTokenProvider)}*", "the missing registration must be named, not swallowed");
        provider.GetRequiredService<SiblingClient>().CallInvoker.Should().NotBeNull(
            "a client that did not opt in has no dependency on the provider");
    }

    [Fact]
    public async Task WithServiceToken_WithAddServiceTokenClient_CreatesTheClient()
    {
        var services = new ServiceCollection();
        services.AddServiceTokenClient(new ConfigurationBuilder()
            .AddInMemoryCollection(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key()))
            .Build());
        services.AddTypedGrpcClient<OptedInClient>("catalog").WithServiceToken();
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<OptedInClient>();

        client.CallInvoker.Should().NotBeNull();
    }

    // -- Typed-client stand-ins: the factory activates them with the intercepted CallInvoker --
    private sealed class OptedInClient(CallInvoker callInvoker)
    {
        public CallInvoker CallInvoker { get; } = callInvoker;
    }

    private sealed class SiblingClient(CallInvoker callInvoker)
    {
        public CallInvoker CallInvoker { get; } = callInvoker;
    }
}
