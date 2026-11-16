using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// Run 17 X-27: a MAUI relaunch right after a theme switch paints the new theme from the first frame.
/// The WebView flushes localStorage lazily, so the pre-paint script prefers the mode the native side
/// saved on every change, which <c>MainPageBase</c> hands it as a user agent token. The two files share
/// only the token name, so these tests pin it; the first frame itself is a device check.
/// </summary>
public sealed class NativeThemePrepaintTests
{
    [Fact]
    public void MainPage_SendsTheSavedNativeThemeAsAUserAgentToken()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        page.Should().Contain("\" MmcaTheme/dark\"").And.Contain("\" MmcaTheme/light\"");
        page.Should().Contain("ThemeToken()");
    }

    [Fact]
    public void MainPage_AppliesTheStoredThemeBeforeTheWebViewPaints()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        // Store Run 1 C8: the native page behind the BlazorWebView showed the OS theme until Blazor's
        // first render. The constructor applies the persisted Day/Dark mode first, so the page starts in
        // it; a call moved to a later lifecycle hook brings the flash back.
        var constructor = page.IndexOf("protected MainPageBase()", StringComparison.Ordinal);
        constructor.Should().BeGreaterThanOrEqualTo(0, "MainPageBase keeps its parameterless constructor");
        var constructorBody = page[constructor..page.IndexOf("protected abstract", constructor, StringComparison.Ordinal)];

        constructorBody.Should().Contain(
            "MauiThemeStore.ApplyStoredTheme()",
            "Store Run 1 C8: the stored theme is applied in the constructor, before the WebView paints");
    }

    [Fact]
    public void PrepaintScript_PrefersTheNativeTokenOnTheSessionsFirstPage()
    {
        var script = ReadEmbedded("theme-prepaint.js");

        script.Should().Contain(@"/MmcaTheme\/(dark|light)/");
        script.Should().Contain("sessionStorage.getItem('mmca_theme_prepainted')");
        script.IndexOf("MmcaTheme", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("localStorage.getItem(key)", StringComparison.Ordinal),
                "the native value is read before the lazily flushed WebView storage");
    }

    [Fact]
    public void MainPage_SendsTheThemeTokenOnIosAndMacCatalystToo()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        page.Should().Contain("#if IOS || MACCATALYST");
        page.Should().Contain("ApplicationNameForUserAgent", "WKWebView fixes its user agent at creation, so the token rides the configuration");
        page.Should().Contain("#if ANDROID || IOS || MACCATALYST", "ThemeToken is shared by both platform hooks");
    }

    [Fact]
    public void PrepaintScript_StampsTheColorModeOnlyWhenTheScriptTagOptsIn()
    {
        var script = ReadEmbedded("theme-prepaint.js");

        // Store Run 4 X-30: a head keying its own dark roles on data-mmca-color-mode got them only after
        // the first interactive render, so its first frame was light.
        script.Should().Contain("script.hasAttribute('data-stamp-color-mode')");
        script.Should().Contain("setAttribute('data-mmca-color-mode', theme)");
        script.IndexOf("setAttribute('data-mmca-color-mode', theme)", StringComparison.Ordinal)
            .Should().BeGreaterThan(script.IndexOf("setAttribute('data-mmca-theme', theme)", StringComparison.Ordinal),
                "both attributes carry the one resolved theme");
    }

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(NativeThemePrepaintTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
