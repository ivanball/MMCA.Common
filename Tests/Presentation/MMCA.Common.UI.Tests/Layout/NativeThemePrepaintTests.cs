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
    public void PrepaintScript_PrefersTheNativeTokenOnTheSessionsFirstPage()
    {
        var script = ReadEmbedded("theme-prepaint.js");

        script.Should().Contain(@"/MmcaTheme\/(dark|light)/");
        script.Should().Contain("sessionStorage.getItem('mmca_theme_prepainted')");
        script.IndexOf("MmcaTheme", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("localStorage.getItem(key)", StringComparison.Ordinal),
                "the native value is read before the lazily flushed WebView storage");
    }

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(NativeThemePrepaintTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
