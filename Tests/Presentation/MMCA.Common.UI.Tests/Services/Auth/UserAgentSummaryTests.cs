using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Pins <see cref="UserAgentSummary"/>, the two-word device label behind the signed-in devices page.
/// The rules worth guarding are all about ORDER: every Chromium browser also claims "Chrome", Chrome
/// and Edge both claim "Safari", Android claims "Linux", and an iPad claims "Macintosh", so the most
/// specific token has to win. Anything unrecognized must report <see langword="null"/> rather than
/// guess, because the page has its own localized "unrecognized device" wording for that.
/// </summary>
public sealed class UserAgentSummaryTests
{
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
}
