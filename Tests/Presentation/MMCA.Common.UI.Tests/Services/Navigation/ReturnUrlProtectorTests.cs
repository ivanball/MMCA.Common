using AwesomeAssertions;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Tests.Services.Navigation;

public class ReturnUrlProtectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_ReturnsFallback(string? candidate)
    {
        var result = ReturnUrlProtector.Sanitize(candidate, "/home");

        result.Should().Be("/home");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/orders")]
    [InlineData("/orders/42")]
    [InlineData("/orders/42?tab=summary")]
    [InlineData("/orders/42#payments")]
    [InlineData("/conference/sessions?q=ai&track=ml")]
    [InlineData("/path/with%20encoded")]
    [InlineData("/javascript:alert(1)")] // path component, not a scheme — safe
    public void Sanitize_SafeRelativePaths_ReturnsCandidate(string candidate)
    {
        var result = ReturnUrlProtector.Sanitize(candidate);

        result.Should().Be(candidate);
    }

    [Theory]
    // Protocol-relative URLs (would redirect off-host)
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("///evil.com")]
    // Backslash sequences (Chrome normalises "/\\" to "//")
    [InlineData("/\\evil.com")]
    [InlineData("/\\\\evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("/orders\\..\\evil")]
    // Absolute URLs and dangerous schemes
    [InlineData("http://evil.com")]
    [InlineData("https://evil.com")]
    [InlineData("https://evil.com/orders/42")]
    [InlineData("ftp://evil.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    // Control characters (header injection / response splitting)
    [InlineData("/orders\r\nSet-Cookie: x=1")]
    [InlineData("/orders\nSet-Cookie: x=1")]
    [InlineData("/orders\rfoo")]
    [InlineData("/orders\tfoo")]
    [InlineData("/orders\0foo")]
    // Non-rooted paths (no leading '/')
    [InlineData("orders/42")]
    [InlineData("./orders")]
    [InlineData("../orders")]
    [InlineData(" /orders")]
    [InlineData("?returnUrl=evil")]
    [InlineData("#fragment")]
    public void Sanitize_UnsafeCandidates_ReturnsFallback(string candidate)
    {
        var result = ReturnUrlProtector.Sanitize(candidate);

        result.Should().Be("/");
    }

    [Fact]
    public void Sanitize_CustomFallback_IsHonoured()
    {
        var result = ReturnUrlProtector.Sanitize("//evil.com", "/dashboard");

        result.Should().Be("/dashboard");
    }
}
