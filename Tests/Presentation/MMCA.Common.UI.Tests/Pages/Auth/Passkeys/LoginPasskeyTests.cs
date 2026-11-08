using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Passkeys;

/// <summary>
/// The sign-in page's "Sign in with a passkey" button (ADR-139): offered only when the host turns it on
/// (<c>SignIn:OfferPasskeys</c>) AND the head can run WebAuthn (<see cref="IPasskeyCapability"/>), so
/// the MAUI head, which keeps the unavailable capability, never shows it; a click runs the ceremony and
/// lands the user signed in.
/// </summary>
public sealed class LoginPasskeyTests : BunitTestBase
{
    private const string SignInButton = "[data-testid='login-passkey-sign-in']";

    private readonly Mock<IPasskeyUIService> _passkeys = new();
    private readonly Mock<IPasskeyCapability> _capability = new();

    public LoginPasskeyTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        var preferences = new Mock<IUserPreferenceReader>();
        preferences.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UserPreferences(null, null));
        Services.AddSingleton(preferences.Object);
        Services.AddSingleton(_passkeys.Object);
    }

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void TheButton_IsHidden_WhenTheCapabilityIsUnavailable()
    {
        Offer();
        Services.AddSingleton<IPasskeyCapability, UnavailablePasskeyCapability>();

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll(SignInButton).Should().BeEmpty("a MAUI head keeps the unavailable capability");
    }

    [Fact]
    public void TheButton_IsHidden_WhenNoCapabilityIsRegistered()
    {
        Offer();

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll(SignInButton).Should().BeEmpty();
    }

    [Fact]
    public void TheButton_IsHidden_WhenTheHostDoesNotOfferPasskeys()
    {
        Available();

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll(SignInButton).Should().BeEmpty();
    }

    [Fact]
    public void TheButton_RunsTheCeremony_AndLandsTheUserSignedIn()
    {
        Offer();
        Available();
        var options = JsonSerializer.SerializeToElement(new { challenge = "abc" });
        var credential = JsonSerializer.SerializeToElement(new { id = "cred" });
        _passkeys.Setup(x => x.BeginSignInAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PasskeyOptionsResponse("flow-1", options)));
        _capability.Setup(x => x.GetAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>())).ReturnsAsync(credential);
        _passkeys.Setup(x => x.SignInAsync(It.IsAny<PasskeySignInRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AuthenticationResponse("access", string.Empty, DateTime.UtcNow.AddMinutes(10))));
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });
        cut.Find(SignInButton).Click();

        cut.WaitForAssertion(() => Navigation.Uri.Should().Be("http://localhost/"));
        _passkeys.Verify(
            x => x.SignInAsync(It.Is<PasskeySignInRequest>(r => r.FlowId == "flow-1" && r.Credential.GetProperty("id").GetString() == "cred"), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void TheButton_WhenTheUserCancels_StaysOnThePageAndSignsNothingIn()
    {
        Offer();
        Available();
        _passkeys.Setup(x => x.BeginSignInAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PasskeyOptionsResponse("flow-1", JsonSerializer.SerializeToElement(new { challenge = "abc" }))));
        _capability.Setup(x => x.GetAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>())).ReturnsAsync((JsonElement?)null);
        Navigation.NavigateTo("/login");

        var cut = RenderUnderTest<Login>(_ => { });
        cut.Find(SignInButton).Click();

        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().ContainSingle());
        Navigation.Uri.Should().EndWith("/login");
        _passkeys.Verify(x => x.SignInAsync(It.IsAny<PasskeySignInRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private void Offer() => Services.AddSingleton(Options.Create(new SignInSettings { OfferPasskeys = true }));

    private void Available()
    {
        _capability.SetupGet(x => x.IsAvailable).Returns(true);
        Services.AddSingleton(_capability.Object);
    }
}
