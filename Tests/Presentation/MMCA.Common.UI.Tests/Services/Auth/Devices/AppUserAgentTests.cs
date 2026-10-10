using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Devices;

namespace MMCA.Common.UI.Tests.Services.Auth.Devices;

/// <summary>
/// Pins <see cref="AppUserAgent.Build"/>, the <c>User-Agent</c> a native MMCA app sends so the
/// signed-in devices list can name the app instead of reporting "Unrecognized device". Three things
/// are contract: the <c>Name/Version (Platform OsVersion; MmcaApp)</c> shape, the <c>MmcaApp</c>
/// marker that <see cref="UserAgentSummary"/> keys on, and a value that is always a legal header no
/// matter what the app's display name contains (spaces, parentheses, non-ASCII).
/// </summary>
public sealed class AppUserAgentTests
{
    // == Shape ==
    [Fact]
    public void Build_WithEveryPart_ProducesTheProductTokenAndAPlatformCommentCarryingTheMarker() =>
        AppUserAgent.Build("AtlDevCon", "1.9.2", "Android", "15")
            .Should().Be("AtlDevCon/1.9.2 (Android 15; MmcaApp)");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_WithNoOsVersion_LeavesThePlatformAloneInTheComment(string? osVersion) =>
        AppUserAgent.Build("AtlDevCon", "1.9.2", "iOS", osVersion)
            .Should().Be("AtlDevCon/1.9.2 (iOS; MmcaApp)");

    [Fact]
    public void Marker_IsTheTokenTheDeviceLabelKeysOn() =>
        AppUserAgent.Marker.Should().Be("MmcaApp");

    [Theory]
    [InlineData("Android", "15")]
    [InlineData("iOS", "18.0")]
    [InlineData("iPadOS", "18.0")]
    [InlineData("Windows", "10.0.22631")]
    [InlineData("macOS", "15.0")]
    public void Build_OnEverySupportedPlatform_CarriesTheMarkerInALegalHeader(string platform, string osVersion)
    {
        var userAgent = AppUserAgent.Build("AtlDevCon", "1.9.2", platform, osVersion);

        userAgent.Should().Contain("MmcaApp");
        AssertLegalUserAgentHeader(userAgent);
    }

    // == Header safety ==
    [Theory]
    // A space would split the product token into two products.
    [InlineData("ADC Mobile")]
    // Parentheses open and close a comment, so they cannot appear in a product token.
    [InlineData("AtlDevCon (Beta)")]
    // A slash separates name from version.
    [InlineData("Atl/Dev/Con")]
    // Header values are ASCII; a display name is not.
    [InlineData("Café Conférence")]
    [InlineData("Конференция ADC")]
    [InlineData("ADC \U0001F389")]
    [InlineData("Tab\tName")]
    public void Build_WithAnAppNameThatIsNotAHeaderToken_StillProducesALegalHeaderWithTheMarker(string appName)
    {
        var userAgent = AppUserAgent.Build(appName, "1.9.2", "Android", "15");

        AssertLegalUserAgentHeader(userAgent);
        userAgent.Should().Contain("MmcaApp");

        var product = ParseUserAgent(userAgent)[0].Product;
        product.Should().NotBeNull("the header must still open with the app's product token");
        product!.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("1.9.2 beta")]
    [InlineData("1.9.2 (42)")]
    [InlineData("1.9.2-ñ")]
    public void Build_WithAVersionThatIsNotAHeaderToken_StillProducesALegalHeader(string appVersion)
    {
        var userAgent = AppUserAgent.Build("AtlDevCon", appVersion, "Android", "15");

        AssertLegalUserAgentHeader(userAgent);
        userAgent.Should().Contain("MmcaApp");
    }

    [Theory]
    // Unbalanced or nested parentheses would end the comment early and orphan the marker.
    [InlineData("15 (Build AP3A")]
    [InlineData("15) (beta")]
    [InlineData("15; MmcaApp")]
    [InlineData("15 Ünïcode")]
    public void Build_WithAnOsVersionThatWouldBreakTheComment_StillProducesALegalHeaderWithTheMarker(string osVersion)
    {
        var userAgent = AppUserAgent.Build("AtlDevCon", "1.9.2", "Android", osVersion);

        AssertLegalUserAgentHeader(userAgent);
        ParseUserAgent(userAgent)
            .Where(p => p.Comment is not null)
            .Should().Contain(p => p.Comment!.Contains("MmcaApp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_WithABlankAppName_Throws(string appName)
    {
        // A blank name would leave "/1.9.2", which is not a product token at all.
        var act = () => AppUserAgent.Build(appName, "1.9.2", "Android", "15");

        act.Should().Throw<ArgumentException>();
    }

    private static List<System.Net.Http.Headers.ProductInfoHeaderValue> ParseUserAgent(string userAgent)
    {
        using var request = new HttpRequestMessage();
        request.Headers.UserAgent.TryParseAdd(userAgent).Should().BeTrue($"'{userAgent}' must parse as a User-Agent header");
        return [.. request.Headers.UserAgent];
    }

    private static void AssertLegalUserAgentHeader(string userAgent)
    {
        userAgent.Should().NotBeNullOrWhiteSpace();
        userAgent.Should().MatchRegex("^[\\x20-\\x7E]+$", "a header value carries visible ASCII only");

        using var request = new HttpRequestMessage();
        request.Headers.UserAgent.TryParseAdd(userAgent).Should().BeTrue(
            $"'{userAgent}' must be accepted by the strict User-Agent parser, which is what HttpClient applies");
    }
}
