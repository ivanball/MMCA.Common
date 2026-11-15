using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// Store manual test Run 4, O14: on Android the status bar kept white icons over Store's light pink
/// app bar, because nothing set the icon appearance. <c>MainPageBase</c> (MMCA.Common.UI.Maui, a MAUI
/// TFM this test project cannot reference) now exposes an opt-in override and applies it through the
/// window insets controller. The appearance itself is a device check; these tests pin the contract.
/// </summary>
public sealed class StatusBarIconAppearanceTests
{
    [Fact]
    public void MainPage_DeclaresTheOverride_OnEveryPlatform_DefaultingToNoChange()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        page.Should().Contain("protected virtual bool? UseDarkStatusBarIcons => null;");
        page.IndexOf("protected virtual bool? UseDarkStatusBarIcons", StringComparison.Ordinal)
            .Should().BeLessThan(page.IndexOf("#if ANDROID", StringComparison.Ordinal),
                "the override compiles on every TFM, so a head that overrides it still builds for iOS");
    }

    [Fact]
    public void MainPage_AppliesTheOverride_ThroughTheInsetsController_OnlyWhenItAnswers()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        page.Should().Contain("if (UseDarkStatusBarIcons is not { } darkIcons)");
        page.Should().Contain("WindowCompat.GetInsetsController(window, decorView) is { } insetsController");
        page.Should().Contain("insetsController.AppearanceLightStatusBars = darkIcons;");
    }

    [Fact]
    public void MainPage_ReappliesTheAppearance_WhenTheAppThemeChanges()
    {
        var page = ReadEmbedded("MainPageBase.cs");

        page.Should().Contain("app.RequestedThemeChanged += OnRequestedThemeChanged;");
        page.Should().Contain("MainThread.BeginInvokeOnMainThread(ApplyStatusBarIconAppearance)");
    }

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(StatusBarIconAppearanceTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
