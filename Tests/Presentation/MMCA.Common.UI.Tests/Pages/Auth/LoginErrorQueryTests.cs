using System.Globalization;
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

    // ── Specific refusal codes (U-15 / O-53, ADC local test run 6) ──
    // The completion endpoint forwards the first domain error code, and a few of those codes carry
    // a recovery path the user needs (who to contact, which sign-in to use). Collapsing them into
    // the generic refusal left a locked or already-linked user retrying a sign-in that can never
    // succeed. Each known code has its own localized wording; an unknown code keeps the generic one.
    private const string RefusedEnglish =
        "We could not complete sign-in with that account. Please try again or use another sign-in method.";

    private const string RefusedSpanish =
        "No pudimos completar el inicio de sesión con esa cuenta. Inténtalo de nuevo o usa otro método de inicio de sesión.";

    private const string AccountLockedEnglish =
        "This account has been locked by an administrator. Contact the organizers.";

    private static readonly string[] ProviderNames = ["Google", "Microsoft", "Facebook", "GitHub", "Apple", "LinkedIn", "Twitter"];

    [Fact]
    public void AccountLocked_ShowsTheLockedMessage_NotTheGenericRefusal()
    {
        var text = AlertTextFor("Auth.AccountLocked");

        text.Should().Be(
            AccountLockedEnglish,
            "a locked account must be told it is locked and who to contact, not invited to retry");
    }

    [Fact]
    public void ExternalProviderAlreadyLinked_ShowsAProviderNeutralRecoveryMessage()
    {
        var text = AlertTextFor("Auth.ExternalProviderAlreadyLinked");

        text.Should().NotBe(RefusedEnglish, "the user needs to know the email is linked to another sign-in");
        text.Should().ContainEquivalentOf(
            "forgot password",
            "the recovery path for an account linked to a different provider is the original provider or Forgot password");
        foreach (var provider in ProviderNames)
        {
            text.Should().NotContainEquivalentOf(
                provider,
                "the redirect carries only the code, so the page cannot know which provider the account is linked to");
        }
    }

    [Fact]
    public void ExternalEmailNotVerified_ShowsItsSpecificMessage()
    {
        var text = AlertTextFor("Auth.ExternalEmailNotVerified");

        text.Should().NotBe(RefusedEnglish, "an unverified provider email has its own recovery path");
        text.Should().ContainEquivalentOf(
            "verified",
            "the message must say the provider did not confirm the email as verified");
    }

    [Theory]
    [InlineData("User.LastName.Empty")]
    [InlineData("Some.Unknown.Code")]
    public void AnUnknownCode_StillShowsTheGenericRefusal(string errorCode) =>
        AlertTextFor(errorCode).Should().Be(RefusedEnglish);

    [Theory]
    [InlineData("oauth_failed", "Signing in with that provider failed. Please try again or use another sign-in method.")]
    [InlineData("missing_claims", "The provider did not share the details needed to sign you in, such as your email address. Check the account's privacy settings and try again.")]
    public void TheTransportLevelCodes_KeepTheirExistingMessages(string errorCode, string expected) =>
        AlertTextFor(errorCode).Should().Be(expected);

    [Theory]
    [InlineData("Auth.AccountLocked")]
    [InlineData("Auth.ExternalProviderAlreadyLinked")]
    [InlineData("Auth.ExternalEmailNotVerified")]
    public void InSpanish_EachSpecificCode_ShowsItsOwnSpanishMessage(string errorCode)
    {
        var englishText = AlertTextFor(errorCode);

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
        try
        {
            var spanishText = AlertTextFor(errorCode);

            spanishText.Should().NotBe(RefusedSpanish, "the Spanish resource must carry the specific message too");
            spanishText.Should().NotBe(englishText, "the specific message must have a Spanish translation");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private string AlertTextFor(string errorCode)
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/login?error=" + Uri.EscapeDataString(errorCode));

        var cut = RenderUnderTest<Login>(_ => { });

        var alerts = cut.FindAll("[role='alert']");
        alerts.Should().ContainSingle("a refused external sign-in shows exactly one alert");
        return alerts[0].TextContent.Trim();
    }
}
