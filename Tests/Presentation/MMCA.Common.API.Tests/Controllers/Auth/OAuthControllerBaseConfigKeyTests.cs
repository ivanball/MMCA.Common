using AwesomeAssertions;
using MMCA.Common.API.Controllers;

namespace MMCA.Common.API.Tests.Controllers.Auth;

/// <summary>
/// Pins the configuration key the OAuth callback reads its UI redirect base from. The AppHost helper
/// <c>WithUiLinks</c> writes the environment form of this key, so the constant is the one place the
/// name is spelled and both sides are asserted against it.
/// </summary>
public sealed class OAuthControllerBaseConfigKeyTests
{
    [Fact]
    public void UIBaseUrlConfigKey_IsOAuthUIBaseUrl()
    {
        OAuthControllerBase.UIBaseUrlConfigKey.Should().Be("OAuth:UIBaseUrl");
    }
}
