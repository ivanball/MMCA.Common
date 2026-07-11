using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Covers <see cref="WasmFormFactor"/> and its <c>AddWasmFormFactor()</c> registration (ADR-042):
/// the WASM client head resolves a singleton <see cref="IFormFactor"/> reporting "WebAssembly"
/// plus the runtime-reported OS description. The Blazor Server head registers
/// <c>AddCommonWebFormFactor()</c> (MMCA.Common.UI.Web) instead.
/// </summary>
public sealed class WasmFormFactorTests
{
    [Fact]
    public void AddWasmFormFactor_RegistersIFormFactorAsASingletonWasmFormFactor()
    {
        var services = new ServiceCollection();

        services.AddWasmFormFactor();

        using var provider = services.BuildServiceProvider();
        var formFactor = provider.GetRequiredService<IFormFactor>();
        formFactor.Should().BeOfType<WasmFormFactor>();
        provider.GetRequiredService<IFormFactor>().Should().BeSameAs(formFactor, "the registration is a singleton");
        services.Should().ContainSingle(d => d.ServiceType == typeof(IFormFactor))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void WasmFormFactor_ReportsWebAssemblyAndTheOsDescription()
    {
        var sut = new WasmFormFactor();

        sut.GetFormFactor().Should().Be("WebAssembly");
        sut.GetPlatform().Should().NotBeNullOrWhiteSpace("the browser-reported OS description backs the platform string");
    }
}
