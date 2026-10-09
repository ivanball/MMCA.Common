using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Theme;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// X-27 (ADC local test run 8): on the MAUI head the native store (device preferences, surfaced as
/// <see cref="IInitialThemeModeSource"/>) held Light while the WebView's cookie/localStorage still
/// held a stale Dark. The first frame painted Light from the native source, then
/// <see cref="ThemeService.InitializeAsync"/> adopted the WebView's Dark after the first render, and
/// the MAUI <c>NativeThemeSync</c> mirrored <see cref="ThemeService.IsDarkMode"/> back into the native
/// store, overwriting the user's Light. On a head with a native source, that source is authoritative
/// at startup. Without one (every web head) the WebView value still wins, exactly as before.
/// <para>
/// <c>NativeThemeSync</c> lives in the MAUI-TFM package, which has no test project; it writes
/// <see cref="ThemeService.IsDarkMode"/> into the native store on every change, so "the native store
/// is not overwritten with Dark" is asserted here as "the service never resolves to Dark".
/// </para>
/// </summary>
public sealed class MmcaThemeProvidersNativeAuthorityTests : BunitTestBase
{
    [Fact]
    public async Task WithANativeLightSource_AStaleWebViewDark_DoesNotWinAfterTheFirstRender()
    {
        Services.AddSingleton<IInitialThemeModeSource>(new FixedInitialThemeModeSource(false));
        var module = WebViewStoreHolds("dark");

        var cut = RenderInteractive();

        // markInteractive is the last step of the first OnAfterRenderAsync, so once it has run the
        // startup theme resolution is over, whichever path the head took to get there.
        await cut.WaitForAssertionAsync(() => module.Invocations["markInteractive"].Should().NotBeEmpty());

        ThemeProviderIsDark(cut).Should().BeFalse(
            "the native store holds Light and is authoritative at startup on a head that has one");
        Services.GetRequiredService<ThemeService>().IsDarkMode.Should().BeFalse(
            "NativeThemeSync mirrors this value into the native store, so Dark here overwrites the user's Light");
        module.Invocations["set"]
            .Select(invocation => invocation.Arguments[0] as string)
            .Should().AllBe(
                "light",
                "if the startup path reseeds the WebView store it must reseed it from the native Light, never Dark");
    }

    [Fact]
    public async Task WithoutANativeSource_TheWebViewValueStillWins()
    {
        var module = WebViewStoreHolds("dark");

        var cut = RenderInteractive();
        await cut.WaitForAssertionAsync(() => module.Invocations["markInteractive"].Should().NotBeEmpty());

        ThemeProviderIsDark(cut).Should().BeTrue(
            "a web head has no native store, so the cookie/localStorage preference decides as before");
        Services.GetRequiredService<ThemeService>().IsDarkMode.Should().BeTrue();
    }

    private BunitJSModuleInterop WebViewStoreHolds(string stored)
    {
        var module = JSInterop.SetupModule(MmcaThemeProvidersTests.ThemeModulePath);
        module.Setup<string?>("get").SetResult(stored);
        module.Setup<bool>("systemPrefersDark").SetResult(false);
        module.SetupVoid("set", _ => true).SetVoidResult();
        module.SetupVoid("markInteractive").SetVoidResult();
        return module;
    }

    private IRenderedComponent<MmcaThemeProviders> RenderInteractive()
    {
        // After every Services.Add call: SetRendererInfo freezes the service provider.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MmcaThemeProviders>(_ => { });
    }

    // The value the component bound into MudThemeProvider. MUD0012 guards against a component reading
    // another's parameter state at run time; a test reading what was bound is exactly the observable.
#pragma warning disable MUD0012
    private static bool ThemeProviderIsDark(IRenderedComponent<MmcaThemeProviders> cut) =>
        cut.FindComponent<MudThemeProvider>().Instance.IsDarkMode;
#pragma warning restore MUD0012

    private sealed class FixedInitialThemeModeSource(bool? isDarkMode) : IInitialThemeModeSource
    {
        public bool? IsDarkMode => isDarkMode;
    }
}
