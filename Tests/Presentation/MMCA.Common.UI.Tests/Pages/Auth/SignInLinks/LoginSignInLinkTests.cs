using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Navigation;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.SignInLinks;

/// <summary>
/// The sign-in page's two newer ways forward (ADR-116): arriving from registration with an address
/// that already has an account prefills it and offers Reset password with the address carried over,
/// and the opt-in "Email me a sign-in link" button asks for a link to the typed address.
/// </summary>
public sealed class LoginSignInLinkTests : BunitTestBase
{
    private const string Email = "ada@example.com";

    private readonly Mock<ISignInLinkUIService> _signInLinks = new();

    public LoginSignInLinkTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
        Services.AddSingleton(_signInLinks.Object);
        _signInLinks
            .Setup(x => x.RequestSignInLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void ArrivingFromRegistrationWithATakenAddress_PrefillsItAndShowsTheNotice()
    {
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", Email, existingAccount: true);

        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-existing-account']").TextContent.Should().Contain(Email);
        cut.Find("input[autocomplete='email']").GetAttribute("value").Should().Be(Email);
    }

    [Fact]
    public void ArrivingWithNoHandedOverAddress_ShowsNoNotice()
    {
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[data-testid='login-existing-account']").Should().BeEmpty();
    }

    [Fact]
    public void ResetPassword_OpensForgotPasswordWithTheAddressInHistoryState()
    {
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", Email, existingAccount: true);
        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-reset-password']").Click();

        Navigation.Uri.Should().EndWith("/forgot-password");
        Navigation.Uri.Should().NotContain(Email, "the address must stay out of the URL");
        AuthEmailPrefill.Read(Navigation, out var existingAccount).Should().Be(Email);
        existingAccount.Should().BeFalse("Forgot password only prefills; it has no notice of its own");
    }

    [Fact]
    public void TheSignInLinkButton_IsAbsentUnlessTheHostOffersIt()
    {
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[data-testid='login-email-sign-in-link']").Should().BeEmpty();
    }

    [Fact]
    public void TheSignInLinkButton_AsksForALinkToTheTypedAddress()
    {
        Services.AddSingleton(Options.Create(new SignInSettings { OfferSignInLink = true }));
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", Email);
        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-email-sign-in-link']").Click();

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='login-sign-in-link-sent']").TextContent.Should().Contain(Email));
        _signInLinks.Verify(x => x.RequestSignInLinkAsync(Email, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void TheSignInLinkButton_WithNoAddress_AsksForOneAndSendsNothing()
    {
        Services.AddSingleton(Options.Create(new SignInSettings { OfferSignInLink = true }));
        Navigation.NavigateTo("/login");
        var cut = RenderUnderTest<Login>(_ => { });

        cut.Find("[data-testid='login-email-sign-in-link']").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().ContainSingle());
        _signInLinks.Verify(
            x => x.RequestSignInLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }
}
