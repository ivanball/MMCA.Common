using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Theme;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers <see cref="MmcaThemeProviders"/>, the single root-layout component owning the four
/// MudBlazor providers plus the Day/Dark lifecycle (ADR-028): all four providers render with the
/// MMCA theme, the first interactive render initializes <see cref="ThemeService"/> (JS interop is
/// unavailable during SSR prerender), <c>OnChange</c> flips the theme provider's dark mode, and
/// disposal unsubscribes so a long-lived scoped service never calls back into a dead component.
/// </summary>
public sealed class MmcaThemeProvidersTests : BunitTestBase
{
    private static readonly FieldInfo OnChangeField = typeof(ThemeService)
        .GetField("OnChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Render_ProducesAllFourMudProviders_WithTheMmcaTheme()
    {
        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });

        cut.FindComponents<MudThemeProvider>().Should().ContainSingle()
            .Which.Instance.Theme.Should().BeSameAs(MMCATheme.Instance);
        cut.FindComponents<MudPopoverProvider>().Should().ContainSingle();
        cut.FindComponents<MudDialogProvider>().Should().ContainSingle();
        cut.FindComponents<MudSnackbarProvider>().Should().ContainSingle();
    }

    [Fact]
    public void FirstInteractiveRender_InitializesTheThemeService()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        themeService.IsInitialized.Should().BeFalse("nothing may touch JS interop before the first render");

        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });

        cut.WaitForAssertion(() => themeService.IsInitialized.Should().BeTrue(
            "OnAfterRenderAsync(firstRender) must resolve the stored/OS preference"));
        themeService.IsDarkMode.Should().BeFalse(
            "loose JSInterop reports no stored value and no OS dark preference");
    }

    [Fact]
    public async Task ThemeServiceChange_FlipsTheThemeProviderIntoDarkModeAndBack()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });
        var themeProvider = cut.FindComponent<MudThemeProvider>();
        string lightMarkup = themeProvider.Markup;

        await cut.InvokeAsync(() => themeService.SetDarkModeAsync(true));
        await cut.WaitForAssertionAsync(() => themeProvider.Markup.Should().NotBe(
            lightMarkup, "the dark palette must flow into the bound MudThemeProvider"));

        await cut.InvokeAsync(() => themeService.SetDarkModeAsync(false));
        await cut.WaitForAssertionAsync(() => themeProvider.Markup.Should().Be(
            lightMarkup, "flipping back must restore the light palette"));
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheThemeServiceOnChangeEvent()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        RenderUnderTest<MmcaThemeProviders>(_ => { });
        OnChangeField.GetValue(themeService).Should().NotBeNull("the component subscribes in OnInitialized");

        await DisposeComponentsAsync();

        OnChangeField.GetValue(themeService).Should().BeNull(
            "the scoped ThemeService outlives the component, so Dispose must unsubscribe");
    }
}
