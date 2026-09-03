using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Theme;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// Covers <see cref="MmcaThemeProviders"/>, the single root-layout component owning the four
/// MudBlazor providers plus the Day/Dark lifecycle (ADR-028): all four providers render with the
/// MMCA theme, the first interactive render initializes <see cref="ThemeService"/> (JS interop is
/// unavailable during SSR prerender), <c>OnChange</c> flips the theme provider's dark mode, and
/// disposal unsubscribes so a long-lived scoped service never calls back into a dead component.
/// The non-interactive (prerender) half of the E2E interactivity marker lives in
/// <see cref="MmcaThemeProvidersPrerenderTests"/>, because <c>SetRendererInfo</c> is a per-context
/// setting.
/// </summary>
public sealed class MmcaThemeProvidersTests : BunitTestBase
{
    /// <summary>The RCL module the component imports to stamp the E2E interactivity marker.</summary>
    internal const string ThemeModulePath = "./_content/MMCA.Common.UI/theme.js";

    private static readonly FieldInfo OnChangeField = typeof(ThemeService)
        .GetField("OnChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo IsDarkModeField = typeof(MmcaThemeProviders)
        .GetField("_isDarkMode", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // The component reads RendererInfo on its first render (it stamps the E2E interactivity marker only
    // on an interactive one), and bUnit throws MissingRendererInfoException when nothing has declared
    // it. Set once here so it covers every test in this class; it must run after the base constructors'
    // Services.Add calls (it freezes the service provider), which it does.
    public MmcaThemeProvidersTests()
        => SetRendererInfo(new RendererInfo("Server", isInteractive: true));

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
    public void Render_HonoursAnAppSuppliedThemeOverride()
    {
        // The Theme parameter is the extension point for a downstream brand: an app passes its own
        // derived MudTheme instead of duplicating the whole provider block. Defaulting to
        // MMCATheme.Instance (asserted above) keeps that addition non-breaking.
        var appTheme = new MudTheme();

        var cut = RenderUnderTest<MmcaThemeProviders>(p => p.Add(c => c.Theme, appTheme));

        cut.FindComponent<MudThemeProvider>().Instance.Theme.Should().BeSameAs(appTheme);
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
    public void FirstInteractiveRender_StampsTheE2eInteractivityMarker()
    {
        // The marker (data-mmca-interactive on the document element) is what
        // MMCA.Common.Testing.E2E's WaitForBlazorAsync gates on: blazor.web.js populates
        // window.Blazor._internal BEFORE components attach their event handlers, so a test gated on
        // that alone clicks prerendered-but-dead controls. Keep the identifier in step with theme.js
        // and PageExtensions.InteractiveMarkerPredicate.
        var themeModule = JSInterop.SetupModule(ThemeModulePath);

        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });

        cut.WaitForAssertion(() => themeModule.Invocations["markInteractive"].Should().ContainSingle(
            "an interactive first render must stamp the marker the E2E interactivity gate waits on"));
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

    [Fact]
    public async Task ThemeChangeRaisedAfterDisposal_IsIgnored_AndDoesNotThrow()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        var cut = RenderUnderTest<MmcaThemeProviders>(_ => { });
        var instance = cut.Instance;

        // Let the first-render initialization settle so it cannot race the flip below.
        await cut.WaitForAssertionAsync(() => themeService.IsInitialized.Should().BeTrue());

        // OnChange is raised synchronously and its invocation list is captured at the raise, so a
        // subscriber disposed partway through the chain (one scoped ThemeService feeds this component
        // and every ThemeToggle placement) still gets called. Capture the delegate to replay that race.
        var subscribers = (EventHandler?)OnChangeField.GetValue(themeService);
        subscribers.Should().NotBeNull();

        await DisposeComponentsAsync();
        await themeService.SetDarkModeAsync(true);
        IsDarkModeField.GetValue(instance).Should().Be(false, "the disposed component never saw the flip");

        var raise = () => subscribers!.Invoke(themeService, EventArgs.Empty);

        raise.Should().NotThrow(
            "a rerender dispatched onto a disposed component must not surface to the publisher");
        IsDarkModeField.GetValue(instance).Should().Be(false,
            "the disposal guard returns before the handler refreshes state from the service");
    }
}
