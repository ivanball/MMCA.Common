using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers the non-interactive half of <see cref="MmcaThemeProviders"/>'s E2E interactivity marker: a
/// renderer that reports as non-interactive must NOT stamp <c>data-mmca-interactive</c>, because
/// MMCA.Common.Testing.E2E's <c>WaitForBlazorAsync</c> reads that attribute as proof that components
/// have attached their event handlers. Separate from <see cref="MmcaThemeProvidersTests"/> because
/// bUnit's <c>SetRendererInfo</c> configures the whole test context, not a single render.
/// </summary>
public sealed class MmcaThemeProvidersPrerenderTests : BunitTestBase
{
    // Must run after the base constructors' Services.Add calls (it freezes the service provider),
    // which it does.
    public MmcaThemeProvidersPrerenderTests()
        => SetRendererInfo(new RendererInfo("Static", isInteractive: false));

    [Fact]
    public void NonInteractiveRender_DoesNotStampTheE2eInteractivityMarker()
    {
        var themeModule = JSInterop.SetupModule(MmcaThemeProvidersTests.ThemeModulePath);

        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });

        // ThemeService.InitializeAsync is deliberately NOT gated on RendererInfo, so its "get" call is
        // what proves OnAfterRenderAsync has already run: without that anchor an empty marker list
        // would only prove the assertion ran too early.
        cut.WaitForAssertion(() => themeModule.Invocations["get"].Should().NotBeEmpty(
            "the first render always resolves the stored theme preference"));

        themeModule.Invocations["markInteractive"].Should().BeEmpty(
            "a non-interactive renderer has attached no event handlers to advertise");
    }
}
