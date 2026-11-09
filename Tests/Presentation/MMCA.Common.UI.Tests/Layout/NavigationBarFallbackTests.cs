using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// X-27/U-46: on an edge-to-edge Android head the bottom of the page must clear the navigation bar,
/// and <c>env(safe-area-inset-bottom)</c> reads 0 on a cold launch. Three files carry the fallback
/// (the native user agent token, the pre-paint script, the stylesheets) and share only string names,
/// so these tests pin the names. The geometry itself is a device check.
/// </summary>
public sealed class NavigationBarFallbackTests
{
    private const string BottomInset = "max(env(safe-area-inset-bottom, 0px), var(--mmca-navigation-bar-fallback, 0px))";

    [Theory]
    [InlineData("MainLayout.razor.css")]
    [InlineData("NavMenu.razor.css")]
    public void Stylesheet_PadsTheBottomWithTheInsetOrTheNativeFallback(string stylesheet) =>
        ReadEmbedded(stylesheet).Should().Contain(BottomInset);

    [Fact]
    public void PrepaintScript_TurnsTheNativeTokenIntoTheFallbackProperty()
    {
        var script = ReadEmbedded("theme-prepaint.js");

        script.Should().Contain(@"/MmcaNavigationBar\/(\d+)/");
        script.Should().Contain("'--mmca-navigation-bar-fallback'");
    }

    [Fact]
    public void MainPage_SendsTheTokenThePrepaintScriptReads() =>
        ReadEmbedded("MainPageBase.cs").Should().Contain("\"navigation_bar_height\", \"MmcaNavigationBar\"");

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(NavigationBarFallbackTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
