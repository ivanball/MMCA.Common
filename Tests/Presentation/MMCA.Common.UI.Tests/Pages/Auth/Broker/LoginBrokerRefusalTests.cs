using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Broker;

/// <summary>
/// Run 17 U-15 (MAUI): a brokered provider sign-in the server refuses shows the same mapped refusal
/// the web flow shows (<c>/login?error=</c>), not the generic "try again" for a sign-in that can never
/// succeed. Dismissal, and a failure the server did not name, keep the generic message.
/// </summary>
public sealed class LoginBrokerRefusalTests : BunitTestBase
{
    private const string NotVerifiedEnglish =
        "The provider did not confirm this email address as verified. Sign in with your email and password instead.";

    private const string GenericRetryEnglish = "External sign-in did not complete. Please try again.";

    private readonly Mock<IExternalAuthBroker> _broker = new();

    public LoginBrokerRefusalTests()
    {
        var settings = new Mock<IOAuthUISettings>();
        settings.SetupGet(s => s.GoogleEnabled).Returns(true);
        _broker.SetupGet(b => b.IsAvailable).Returns(true);

        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(settings.Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
        Services.AddSingleton(_broker.Object);
    }

    [Fact]
    public async Task AServerRefusal_ShowsTheMappedRefusalText()
    {
        _broker.Setup(b => b.SignInAsync("google", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalSignInResult.Failed("Auth.ExternalEmailNotVerified"));

        var cut = await SignInWithGoogleAsync();

        await cut.WaitForAssertionAsync(() => cut.Find("[role='alert']").TextContent.Trim().Should().Be(NotVerifiedEnglish));
    }

    [Fact]
    public async Task ADismissedSignIn_KeepsTheGenericRetryMessage()
    {
        _broker.Setup(b => b.SignInAsync("google", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalSignInResult.Failed());

        var cut = await SignInWithGoogleAsync();

        await cut.WaitForAssertionAsync(() => cut.Find("[role='alert']").TextContent.Trim().Should().Be(GenericRetryEnglish));
    }

    private async Task<IRenderedComponent<Login>> SignInWithGoogleAsync()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        var google = cut.FindAll("button").Single(b => b.TextContent.Contains("Google", StringComparison.Ordinal));
        await cut.InvokeAsync(() => google.Click());
        return cut;
    }
}
