using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Theme;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// <c>Layout:StampColorModeOnDocument</c>: when on, <see cref="MmcaThemeProviders"/> mirrors the Day/Dark
/// mode onto the document (theme.js <c>stampColorMode</c>) on its first interactive render and on every
/// change, so an app's stylesheet can follow it; when off (the default) it never calls it.
/// </summary>
public sealed class MmcaThemeProvidersColorModeTests : BunitTestBase
{
    [Fact]
    public async Task WhenEnabled_StampsTheModeOnFirstRenderAndOnEveryChange()
    {
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(new LayoutSettings { StampColorModeOnDocument = true }));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var themeModule = JSInterop.SetupModule(MmcaThemeProvidersTests.ThemeModulePath);
        var themeService = Services.GetRequiredService<ThemeService>();

        // The first render stamps, and so may the ThemeService's own startup change event: stamping is
        // idempotent, so the values matter, not the count.
        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });
        await cut.WaitForAssertionAsync(() => themeModule.Invocations["stampColorMode"]
            .Select(call => call.Arguments[0]).Should().NotBeEmpty().And.OnlyContain(mode => Equals(mode, "light")));

        await cut.InvokeAsync(() => themeService.SetDarkModeAsync(true));

        await cut.WaitForAssertionAsync(() => themeModule.Invocations["stampColorMode"]
            .Select(call => call.Arguments[0]).Last().Should().Be("dark", "a toggle re-stamps the new mode"));
    }

    [Fact]
    public async Task WhenNotEnabled_NeverStamps()
    {
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var themeModule = JSInterop.SetupModule(MmcaThemeProvidersTests.ThemeModulePath);
        var themeService = Services.GetRequiredService<ThemeService>();

        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });
        await cut.WaitForAssertionAsync(() => themeModule.Invocations["markInteractive"].Should().ContainSingle());
        await cut.InvokeAsync(() => themeService.SetDarkModeAsync(true));

        themeModule.Invocations["stampColorMode"].Should().BeEmpty("the default leaves the document untouched");
    }
}
