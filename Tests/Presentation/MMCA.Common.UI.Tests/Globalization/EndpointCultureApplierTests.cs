using AwesomeAssertions;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Services.Culture;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Covers the default web <see cref="ICultureApplier"/> (ADR-027): it must route through the server
/// <c>/culture/set</c> endpoint with a force-load, because only that round trip writes the culture cookie
/// that SSR prerender and the WASM runtime both read.
/// </summary>
public sealed class EndpointCultureApplierTests : BunitTestBase
{
    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public async Task ApplyAsync_NavigatesToTheCultureEndpoint_WithTheReturnUrlAsRedirect()
    {
        var applier = new EndpointCultureApplier(Navigation);

        await applier.ApplyAsync("es", "/sessions?page=2");

        Navigation.Uri.Should().Be(
            "http://localhost/culture/set?culture=es&redirectUri=" + Uri.EscapeDataString("/sessions?page=2"));
    }

    [Fact]
    public async Task ApplyAsync_ForcesAFullLoad_SoTheServerRerendersUnderTheNewCookie()
    {
        var applier = new EndpointCultureApplier(Navigation);

        await applier.ApplyAsync("es", "/");

        Navigation.History.Should().ContainSingle().Which.Options.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_DefaultsAnEmptyReturnUrlToRoot()
    {
        var applier = new EndpointCultureApplier(Navigation);

        await applier.ApplyAsync("es", "   ");

        Navigation.Uri.Should().Be(
            "http://localhost/culture/set?culture=es&redirectUri=" + Uri.EscapeDataString("/"));
    }

    [Fact]
    public async Task ApplyAsync_RejectsAMissingCulture()
    {
        var applier = new EndpointCultureApplier(Navigation);

        await Assert.ThrowsAsync<ArgumentException>(() => applier.ApplyAsync("  ", "/"));
    }

    // AddUIShared must keep supplying this implementation via TryAdd: a MAUI head replaces it with a
    // plain Add AFTER AddUIShared, and last-registration-wins only holds while this side stays a TryAdd.
    [Fact]
    public void AddUIShared_RegistersTheEndpointApplier_AsTheDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = "https://localhost:6001" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMudServices();
        services.AddUIShared(configuration);

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(ICultureApplier)).Subject;
        descriptor.ImplementationType.Should().Be<EndpointCultureApplier>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
