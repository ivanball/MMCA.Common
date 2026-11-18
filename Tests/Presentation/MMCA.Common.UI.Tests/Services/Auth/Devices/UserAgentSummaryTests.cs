using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth.Devices;

namespace MMCA.Common.UI.Tests.Services.Auth.Devices;

/// <summary>
/// Pins <see cref="UserAgentSummary"/>, the two-word device label behind the signed-in devices page.
/// The rules worth guarding are all about ORDER: every Chromium browser also claims "Chrome", Chrome
/// and Edge both claim "Safari", Android claims "Linux", and an iPad claims "Macintosh", so the most
/// specific token has to win. Anything unrecognized must report <see langword="null"/> rather than
/// guess, because the page has its own localized "unrecognized device" wording for that.
/// </summary>
public sealed class UserAgentSummaryTests : IDisposable
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly ResourceManager SharedResources = new(typeof(SharedResource));

    private readonly ServiceProvider _services;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public UserAgentSummaryTests()
    {
        // The same registration the bUnit harness makes, so the label reads the shipped neutral resources.
        _services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        _localizer = _services.GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    public void Dispose() => _services.Dispose();

    // == Real headers: the browser half ==
    [Theory]
    // Plain Chrome: the baseline the trickier cases have to beat.
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Chrome",
        "Windows")]
    // Edge says Chrome AND Safari; only the Edg/ token distinguishes it.
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.2592.68",
        "Edge",
        "Windows")]
    // Edge on Android uses a different token again (EdgA/), and the platform must not read as Linux.
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36 EdgA/126.0.0.0",
        "Edge",
        "Android")]
    // Edge on iOS: EdgiOS/, and the header also carries Version/ and Safari/.
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 EdgiOS/126.0.0.0 Mobile/15E148 Safari/605.1.15",
        "Edge",
        "iOS")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Firefox",
        "macOS")]
    // Real Safari must not be reported as Chrome: it carries Safari/ and no Chrome/ at all.
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
        "Safari",
        "macOS")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36",
        "Chrome",
        "Android")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari",
        "iOS")]
    // Chrome on iOS is CriOS/, and there is no Chrome/ token to fall back on.
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.6478.54 Mobile/15E148 Safari/604.1",
        "Chrome",
        "iOS")]
    // Firefox on iOS is FxiOS/, and the header still ends in Safari/.
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/127.0 Mobile/15E148 Safari/605.1.15",
        "Firefox",
        "iOS")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 OPR/112.0.0.0",
        "Opera",
        "Windows")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36",
        "Samsung Internet",
        "Android")]
    // ChromeOS reports X11 + CrOS, never Linux.
    [InlineData(
        "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Chrome",
        "ChromeOS")]
    [InlineData(
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Chrome",
        "Linux")]
    // An iPad says "like Mac OS X" but has its own platform.
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari",
        "iPadOS")]
    public void Parse_WithRealHeader_ReportsBrowserAndPlatform(string userAgent, string browser, string platform)
    {
        var summary = UserAgentSummary.Parse(userAgent);

        summary.Browser.Should().Be(browser);
        summary.Platform.Should().Be(platform);
    }

    // == A header the parser can only half read ==
    [Theory]
    [InlineData("Firefox/127.0", "Firefox", null)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", null, "Windows")]
    // Windows Phone beats the bare Windows token, and IEMobile is not a browser this knows.
    [InlineData("Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; Touch; IEMobile/11.0) like Gecko", null, "Windows Phone")]
    [InlineData("Mozilla/5.0 (iPod touch; CPU iPhone OS 12_5 like Mac OS X)", null, "iOS")]
    public void Parse_WithPartialHeader_ReportsOnlyTheHalfItRecognizes(
        string userAgent, string? browser, string? platform)
    {
        var summary = UserAgentSummary.Parse(userAgent);

        summary.Browser.Should().Be(browser);
        summary.Platform.Should().Be(platform);
    }

    // == Nothing to read ==
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Parse_WithMissingHeader_ReportsNeitherHalf(string? userAgent)
    {
        var summary = UserAgentSummary.Parse(userAgent);

        summary.Browser.Should().BeNull();
        summary.Platform.Should().BeNull();
    }

    [Theory]
    [InlineData("curl/8.7.1")]
    [InlineData("PostmanRuntime/7.39.0")]
    [InlineData("?????")]
    public void Parse_WithUnrecognizableHeader_ReportsNeitherHalf(string userAgent)
    {
        // The page renders its own "unrecognized device" wording for this, so guessing here would
        // be worse than reporting nothing.
        var summary = UserAgentSummary.Parse(userAgent);

        summary.Browser.Should().BeNull();
        summary.Platform.Should().BeNull();
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var summary = UserAgentSummary.Parse("mozilla/5.0 (windows nt 10.0) chrome/126.0.0.0 safari/537.36");

        summary.Browser.Should().Be("Chrome");
        summary.Platform.Should().Be("Windows");
    }

    [Fact]
    public void Parse_ReturnsTheTwoPartsSeparately_NeverAJoinedSentence()
    {
        // The page composes the label through a resource format (ADR-027), so word order stays
        // translatable; a parser that returned "Chrome on Windows" would hard-code English here.
        var summary = UserAgentSummary.Parse(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

        summary.Browser.Should().NotContain(" on ");
        summary.Platform.Should().NotContain(" on ");
    }

    // == Describe: the device label ==
    // Pins Describe(string?, IStringLocalizer) and its device-metadata overload, the device label
    // shared by the signed-in devices page and the administrator's sessions table. A native MMCA app
    // (its user agent carries the MmcaApp marker, see AppUserAgent) reads as "{AppName} app on
    // {Platform}", and the marker wins over any browser token; plain browser headers and an empty
    // header keep the labels the page showed before the app label existed.
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

    // == The device metadata a native client reported ==
    [Fact]
    public void Describe_WithAReportedNameModelAndPlatform_IsTheNameWithItsModelOnThePlatform() =>
        UserAgentSummary.Describe(
                new DeviceMetadata("install-1", "Ana's phone", "Pixel 8", "Google", "Android", "Phone", "Physical"),
                "AtlDevCon/1.9.2 (Android 15; MmcaApp)",
                _localizer)
            .Should().Be("Ana's phone (Pixel 8 on Android)");

    [Fact]
    public void Describe_WithAReportedModelButNoName_IsTheModelOnThePlatform() =>
        UserAgentSummary.Describe(
                new DeviceMetadata("install-1", "  ", "iPhone 15", "Apple", "iOS", "Phone", "Physical"),
                userAgent: null,
                _localizer)
            .Should().Be("iPhone 15 on iOS");

    [Fact]
    public void Describe_WithAReportedNameOnly_IsTheName() =>
        UserAgentSummary.Describe(new DeviceMetadata(null, "Work tablet", null, null, null, null, null), userAgent: null, _localizer)
            .Should().Be("Work tablet");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describe_WithNoUsableDeviceMetadata_FallsBackToTheUserAgentLabel(bool reportedButBlank)
    {
        DeviceMetadata? device = reportedButBlank ? new DeviceMetadata("install-1", " ", null, "Google", null, "Phone", "Physical") : null;

        UserAgentSummary.Describe(device, ChromeOnWindows, _localizer)
            .Should().Be(UserAgentSummary.Describe(ChromeOnWindows, _localizer));
    }

    [Fact]
    public void NamedFormatResource_InBothCultures_KeepsTheNameFirst()
    {
        SharedResources.GetString("Auth.Sessions.Device.NamedFormat", CultureInfo.InvariantCulture).Should().Be("{0} ({1})");
        SharedResources.GetString("Auth.Sessions.Device.NamedFormat", CultureInfo.GetCultureInfo("es")).Should().Be("{0} ({1})");
    }
}
