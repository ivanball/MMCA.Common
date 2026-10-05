using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Theme;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// X-27 (ADC local test run 6): on the MAUI head the app opened in the light palette and flipped to
/// dark a moment later, because <see cref="MmcaThemeProviders"/> only learns the mode in
/// <c>OnAfterRenderAsync</c> through JS interop. A head that already knows the stored preference
/// (MMCA.Common.UI.Maui, from its own store) registers an <see cref="IInitialThemeModeSource"/>,
/// which the component reads synchronously during initialization, so the FIRST render already
/// carries the right palette. With no source registered (or one that reports
/// <see langword="null"/>, "unknown"), behavior is unchanged: light until the JS path resolves.
/// <para>
/// Contract for the implementer: <c>public interface MMCA.Common.UI.Theme.IInitialThemeModeSource</c>
/// with the single member <c>bool? IsDarkMode { get; }</c>. Until that type exists this file is the
/// expected compile failure of the test project.
/// </para>
/// <para>
/// The JS <c>get</c> call is left pending, so the stored/OS preference never resolves and every
/// assertion observes the state the first render produced, not the one the JS path would set later.
/// </para>
/// </summary>
public sealed class MmcaThemeProvidersInitialModeTests : BunitTestBase
{
    [Fact]
    public void WhenAnInitialSourceReportsDark_TheFirstRenderIsAlreadyDark()
    {
        Services.AddSingleton<IInitialThemeModeSource>(new FixedInitialThemeModeSource(true));
        var cut = RenderWithThePreferenceReadPending();

        ThemeProviderIsDark(cut).Should().BeTrue(
            "the head already knows the stored preference, so the first paint must not be the light palette");
    }

    [Fact]
    public void WhenAnInitialSourceReportsLight_TheFirstRenderIsLight()
    {
        Services.AddSingleton<IInitialThemeModeSource>(new FixedInitialThemeModeSource(false));
        var cut = RenderWithThePreferenceReadPending();

        ThemeProviderIsDark(cut).Should().BeFalse();
    }

    [Fact]
    public void WhenAnInitialSourceReportsUnknown_TheFirstRenderFallsBackToLight()
    {
        Services.AddSingleton<IInitialThemeModeSource>(new FixedInitialThemeModeSource(null));
        var cut = RenderWithThePreferenceReadPending();

        ThemeProviderIsDark(cut).Should().BeFalse(
            "an unknown initial mode leaves the decision to the JS path, as before");
    }

    [Fact]
    public void WithNoInitialSourceRegistered_TheFirstRenderIsLight_AsBefore()
    {
        var cut = RenderWithThePreferenceReadPending();

        ThemeProviderIsDark(cut).Should().BeFalse(
            "without a source the mode is resolved only by the JS path after the first render");
    }

    private IRenderedComponent<MmcaThemeProviders> RenderWithThePreferenceReadPending()
    {
        // Leave the stored-preference read unanswered: ThemeService.InitializeAsync (and with it the
        // component's OnAfterRenderAsync) stays suspended, so only the first render's state is visible.
        JSInterop.SetupModule(MmcaThemeProvidersTests.ThemeModulePath).Setup<string?>("get");

        // After every Services.Add call: SetRendererInfo freezes the service provider.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MmcaThemeProviders>(_ => { });
    }

    // The value the component bound into MudThemeProvider on its first render. MUD0012 guards against
    // a component reading another's parameter state at run time; a test reading what was bound is
    // exactly the observable it needs, and the read happens nowhere else.
#pragma warning disable MUD0012
    private static bool ThemeProviderIsDark(IRenderedComponent<MmcaThemeProviders> cut) =>
        cut.FindComponent<MudThemeProvider>().Instance.IsDarkMode;
#pragma warning restore MUD0012

    private sealed class FixedInitialThemeModeSource(bool? isDarkMode) : IInitialThemeModeSource
    {
        public bool? IsDarkMode => isDarkMode;
    }
}
