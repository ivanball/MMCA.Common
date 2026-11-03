using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities.Navigation;

namespace MMCA.Common.UI.Tests.Services.Capabilities;

/// <summary>
/// D5 (Run 15): <see cref="DeepLinkDispatcher.TryComposeRoute"/> checked only the ENCODED route, so a
/// link whose path decodes to a control character, a backslash, a protocol-relative <c>//</c> or a
/// dot-dot segment passed the shape check. An encoded NUL then raised the Blazor unhandled-error bar on
/// MAUI and every later deep link went dead. The decoded path is now checked too, while the route that
/// is published stays in its encoded form.
/// </summary>
public sealed class DeepLinkDecodedPathTests
{
    [Theory]
    [InlineData("/conference/sessions/abc%00")]
    [InlineData("/conference/sessions/abc%0A")]
    [InlineData("/conference/sessions/abc%0d")]
    [InlineData("/conference/sessions/abc%5C")]
    [InlineData("/%5Cattacker.example")]
    [InlineData("/%2Fattacker.example/p")]
    [InlineData("/conference/..%2F..%2Fadmin")]
    [InlineData("/conference/%2E%2E/admin")]
    public void TryComposeRoute_PathThatDecodesToAnUnsafeShape_IsDropped(string encodedPath)
    {
        DeepLinkDispatcher.TryComposeRoute(encodedPath, null, null, out var route).Should().BeFalse();

        route.Should().BeNull();
    }

    [Theory]
    [InlineData("/conference/sessions/42", null, null, "/conference/sessions/42")]
    [InlineData("/catalog/a%3Fb", null, null, "/catalog/a%3Fb")]
    [InlineData("/speakers/Jos%C3%A9%20P%C3%A9rez", null, null, "/speakers/Jos%C3%A9%20P%C3%A9rez")]
    [InlineData("/auth/sign-in-link", null, "email=ada%40example.com&token=a%2Bb", "/auth/sign-in-link#email=ada%40example.com&token=a%2Bb")]
    [InlineData("/search", "q=a%2Fb", null, "/search?q=a%2Fb")]
    [InlineData("/files/v1..2", null, null, "/files/v1..2")]
    public void TryComposeRoute_NormalEncodedRoute_PassesAndStaysEncoded(
        string encodedPath,
        string? encodedQuery,
        string? encodedFragment,
        string expected)
    {
        DeepLinkDispatcher.TryComposeRoute(encodedPath, encodedQuery, encodedFragment, out var route).Should().BeTrue();

        route.Should().Be(expected);
    }
}
