using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Web.Services;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// Covers <see cref="WebFormFactor"/> and the UI.Web registration extensions:
/// <c>AddCommonWebFormFactor()</c> resolves a singleton <see cref="IFormFactor"/> reporting "Web"
/// (the WASM client registers <c>AddWasmFormFactor()</c> from MMCA.Common.UI instead), and the
/// sibling host registrations in the same extension class wire the documented lifetimes
/// (scoped cookie-backed token storage, singleton Blazor CSP provider).
/// </summary>
public sealed class WebFormFactorTests
{
    [Fact]
    public void AddCommonWebFormFactor_RegistersIFormFactorAsASingletonWebFormFactor()
    {
        var services = new ServiceCollection();

        services.AddCommonWebFormFactor();

        using var provider = services.BuildServiceProvider();
        var formFactor = provider.GetRequiredService<IFormFactor>();
        formFactor.Should().BeOfType<WebFormFactor>();
        provider.GetRequiredService<IFormFactor>().Should().BeSameAs(formFactor, "the registration is a singleton");
        services.Should().ContainSingle(d => d.ServiceType == typeof(IFormFactor))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void WebFormFactor_ReportsWebAndTheServerOsDescription()
    {
        var sut = new WebFormFactor();

        sut.GetFormFactor().Should().Be("Web");
        sut.GetPlatform().Should().NotBeNullOrWhiteSpace("the server OS description backs the platform string");
    }

    [Fact]
    public void AddCommonServerTokenStorage_RegistersTheScopedCookieBackedStorageAndTheContextAccessor()
    {
        var services = new ServiceCollection();

        services.AddCommonServerTokenStorage();

        services.Should().ContainSingle(d =>
                d.ServiceType == typeof(ITokenStorageService)
                && d.ImplementationType == typeof(ServerTokenStorageService))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped, "token state is per circuit/request");
        services.Should().Contain(d => d.ServiceType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor));
    }

    [Fact]
    public void AddCommonBlazorCsp_RegistersTheSingletonDynamicCspProvider()
    {
        var services = new ServiceCollection();

        services.AddCommonBlazorCsp();

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(ICspPolicyProvider)).Which;
        descriptor.ImplementationType!.Name.Should().Be(
            "BlazorCspPolicyProvider", "the dynamic provider is internal, so its descriptor is checked by name");
        descriptor.Lifetime.Should().Be(
            ServiceLifetime.Singleton,
            "it must win over the default static provider, which is registered with TryAdd");
    }
}
