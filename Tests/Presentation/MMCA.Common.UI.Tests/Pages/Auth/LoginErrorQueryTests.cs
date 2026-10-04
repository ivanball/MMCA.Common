using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// The OAuth completion endpoint sends every refusal back to <c>/login?error={code}</c>
/// (<c>oauth_failed</c>, <c>missing_claims</c>, or the first domain error code such as
/// <c>User.LastName.Empty</c>). The login page must tell the user the sign-in failed, in the
/// role="alert" live region, in words rather than the raw code. Without the error parameter the page
/// shows no alert at all.
/// </summary>
public sealed class LoginErrorQueryTests : BunitTestBase
{
    public LoginErrorQueryTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
    }

    [Theory]
    [InlineData("oauth_failed")]
    [InlineData("missing_claims")]
    [InlineData("User.LastName.Empty")]
    public void WithAnErrorQueryParameter_ShowsALocalizedAlert_NotTheRawCode(string errorCode)
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/login?error=" + Uri.EscapeDataString(errorCode));

        var cut = RenderUnderTest<Login>(_ => { });

        var alerts = cut.FindAll("[role='alert']");
        alerts.Should().ContainSingle(
            "a refused external sign-in redirects here with ?error=, and the user must be told it failed");
        var text = alerts[0].TextContent.Trim();
        text.Should().NotBeNullOrWhiteSpace("the alert must carry a message");
        text.Should().NotContain(errorCode, "the error code is a machine value; the user reads a localized message");
    }

    [Fact]
    public void WithoutAnErrorQueryParameter_ShowsNoAlert()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[role='alert']").Should().BeEmpty("nothing failed");
    }
}
