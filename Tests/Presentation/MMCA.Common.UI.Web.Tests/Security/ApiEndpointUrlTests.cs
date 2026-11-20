using AwesomeAssertions;
using MMCA.Common.UI.Web.Security;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// The shared API endpoint test behind the CSP, <c>/client-config</c> and <c>AddMmcaBlazorWebHost</c>:
/// an absolute http(s) URL qualifies everywhere, an Aspire service-discovery URL only for the server
/// head's <c>Api:ApiEndpoint</c>, and a blank value, a deployment placeholder, a relative path or another
/// scheme never.
/// </summary>
public sealed class ApiEndpointUrlTests
{
    [Theory]
    [InlineData("https://gateway.example.com")]
    [InlineData("https://localhost:6001/")]
    [InlineData("http://gateway")]
    [InlineData("HTTPS://GATEWAY.EXAMPLE.COM")]
    public void AnAbsoluteHttpUrl_QualifiesForBothKeys(string value)
    {
        ApiEndpointUrl.IsAbsoluteHttp(value).Should().BeTrue();
        ApiEndpointUrl.IsAbsoluteHttpOrServiceDiscovery(value).Should().BeTrue();
        ApiEndpointUrl.TryParseAbsoluteHttp(value, out var uri).Should().BeTrue();
        uri!.IsAbsoluteUri.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SET-VIA-ENVIRONMENT-VARIABLE")]
    [InlineData("gateway")]
    [InlineData("/relative/path")]
    [InlineData("ftp://gateway.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https+ftp://gateway")]
    [InlineData("https+://gateway")]
    public void ANonUrl_QualifiesForNeitherKey(string? value)
    {
        ApiEndpointUrl.IsAbsoluteHttp(value).Should().BeFalse();
        ApiEndpointUrl.IsAbsoluteHttpOrServiceDiscovery(value).Should().BeFalse();
        ApiEndpointUrl.TryParseAbsoluteHttp(value, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    [Theory]
    [InlineData("https+http://gateway")]
    [InlineData("http+https://gateway")]
    public void AServiceDiscoveryUrl_QualifiesOnlyForTheServerHeadKey(string value)
    {
        ApiEndpointUrl.IsAbsoluteHttp(value).Should().BeFalse("a browser cannot resolve a service-discovery name");
        ApiEndpointUrl.IsAbsoluteHttpOrServiceDiscovery(value).Should().BeTrue("the server head's factory clients resolve it");
    }
}
