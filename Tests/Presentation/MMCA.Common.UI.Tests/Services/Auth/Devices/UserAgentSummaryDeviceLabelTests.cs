using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth.Devices;

namespace MMCA.Common.UI.Tests.Services.Auth.Devices;

/// <summary>
/// Pins <see cref="UserAgentSummary.Describe"/>, the device label shared by the signed-in devices page
/// and the administrator's sessions table. A native MMCA app (its user agent carries the
/// <c>MmcaApp</c> marker, see <see cref="AppUserAgent"/>) reads as "{AppName} app on {Platform}",
/// and the marker wins over any browser token; plain browser headers and an empty header keep the
/// labels the page showed before the app label existed.
/// </summary>
public sealed class UserAgentSummaryDeviceLabelTests : IDisposable
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly ResourceManager SharedResources = new(typeof(SharedResource));

    private readonly ServiceProvider _services;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public UserAgentSummaryDeviceLabelTests()
    {
        // The same registration the bUnit harness makes, so the label reads the shipped neutral resources.
        _services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        _localizer = _services.GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    public void Dispose() => _services.Dispose();

    // == The native app label ==
    [Fact]
    public void Describe_WithTheSpecExampleAppUserAgent_IsTheAppNameAppOnThePlatform() =>
        UserAgentSummary.Describe("AtlDevCon/1.9.2 (Android 15; MmcaApp)", _localizer)
            .Should().Be("AtlDevCon app on Android");

    [Theory]
    [InlineData("Android", "15", "Android")]
    [InlineData("iOS", "18.0", "iOS")]
    [InlineData("iPadOS", "18.0", "iPadOS")]
    [InlineData("Windows", "10.0.22631", "Windows")]
    [InlineData("macOS", "15.0", "macOS")]
    public void Describe_WithABuiltAppUserAgent_NamesTheAppAndThePlatform(
        string platform, string osVersion, string expectedPlatform) =>
        UserAgentSummary.Describe(AppUserAgent.Build("AtlDevCon", "1.9.2", platform, osVersion), _localizer)
            .Should().Be($"AtlDevCon app on {expectedPlatform}");

    [Theory]
    // MAUI's DevicePlatform names for the two desktop heads: the label still uses the table's names.
    [InlineData("WinUI", "10.0.22631", "Windows")]
    [InlineData("MacCatalyst", "15.0", "macOS")]
    public void Describe_WithAnAppUserAgentBuiltFromMauiDesktopPlatformNames_UsesTheTablesPlatformName(
        string mauiPlatform, string osVersion, string expectedPlatform) =>
        UserAgentSummary.Describe(AppUserAgent.Build("AtlDevCon", "1.9.2", mauiPlatform, osVersion), _localizer)
            .Should().Be($"AtlDevCon app on {expectedPlatform}");

    [Fact]
    public void Describe_WithTheMarkerAndABrowserToken_PrefersTheAppLabel()
    {
        // The marker is recognized BEFORE the browser tokens: an app UA that also carries a WebView's
        // Chrome/ token is still the app.
        const string userAgent = "AtlDevCon/1.9.2 (Android 15; MmcaApp) AppleWebKit/537.36 Chrome/126.0.0.0 Mobile Safari/537.36";

        UserAgentSummary.Describe(userAgent, _localizer).Should().Be("AtlDevCon app on Android");
    }

    [Fact]
    public void Describe_WithAnAppNameThatNeededSanitizing_StillReadsAsAnAppOnThePlatform()
    {
        var label = UserAgentSummary.Describe(AppUserAgent.Build("ADC Mobile (Beta)", "1.9.2", "iOS", "18.0"), _localizer);

        label.Should().EndWith(" app on iOS");
        label.Should().NotBe(" app on iOS", "the sanitized app name is still shown");
    }

    // == Regression guard: browser and empty headers keep today's labels ==
    [Fact]
    public void Describe_WithAPlainBrowserHeader_KeepsTheBrowserOnPlatformLabel() =>
        UserAgentSummary.Describe(ChromeOnWindows, _localizer).Should().Be("Chrome on Windows");

    [Theory]
    [InlineData("Firefox/127.0", "Firefox")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Windows")]
    public void Describe_WithAHalfReadableBrowserHeader_ShowsTheHalfItRecognizes(string userAgent, string expected) =>
        UserAgentSummary.Describe(userAgent, _localizer).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.7.1")]
    public void Describe_WithAnEmptyOrUnrecognizableHeader_KeepsTheUnrecognizedDeviceLabel(string? userAgent) =>
        UserAgentSummary.Describe(userAgent, _localizer).Should().Be("Unrecognized device");

    // == The resource the app label is composed through (ADR-027) ==
    [Fact]
    public void AppFormatResource_InTheNeutralCulture_IsAppNameAppOnPlatform() =>
        SharedResources.GetString("Auth.Sessions.Device.AppFormat", CultureInfo.InvariantCulture)
            .Should().Be("{0} app on {1}");

    [Fact]
    public void AppFormatResource_InSpanish_PutsTheWordOrderInTheResource() =>
        SharedResources.GetString("Auth.Sessions.Device.AppFormat", CultureInfo.GetCultureInfo("es"))
            .Should().Be("App {0} en {1}");

    [Fact]
    public void AppFormatResource_ThroughTheLocalizer_FormatsTheAppAndPlatform()
    {
        var label = _localizer["Auth.Sessions.Device.AppFormat", "AtlDevCon", "Android"];

        label.ResourceNotFound.Should().BeFalse();
        label.Value.Should().Be("AtlDevCon app on Android");
    }
}
