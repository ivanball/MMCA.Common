using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// X-01 (ADC local test run 8): a failed PASSWORD sign-in rendered the API's English message in the
/// alert even under a Spanish UI, because the page localized the failure by its message text and the
/// API's English sentence is not a resource key. The page must localize the refusal by its error
/// CODE: <c>Auth.InvalidCredentials</c> and <c>Auth.AccountLocked</c> each show the active culture's
/// wording, and under English the English wording still shows.
/// </summary>
public sealed class LoginPasswordErrorLocalizationTests : BunitTestBase
{
    private const string InvalidCredentialsCode = "Auth.InvalidCredentials";
    private const string AccountLockedCode = "Auth.AccountLocked";

    // The server's own messages (AuthenticationServiceBase.LoginAsync; the ADC lockout check).
    private const string InvalidCredentialsServerMessage = "Invalid email or password.";
    private const string AccountLockedServerMessage =
        "This account has been locked by an administrator. Contact the organizers.";

    // SharedResource.es.resx, Auth.Login.InvalidCredentials.
    private const string InvalidCredentialsSpanish = "Correo electrónico o contraseña no válidos.";

    private readonly Mock<IAuthUIService> _auth = new();

    public LoginPasswordErrorLocalizationTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
    }

    [Fact]
    public void InSpanish_InvalidCredentials_ShowsTheSpanishInvalidCredentialsText()
    {
        var text = InCulture("es-ES", () => AlertTextAfterFailedLogin(InvalidCredentialsCode, InvalidCredentialsServerMessage));

        text.Should().Be(
            InvalidCredentialsSpanish,
            "the refusal is localized by its error code, not by the API's English sentence");
    }

    [Fact]
    public void InSpanish_AccountLocked_ShowsASpanishMessage_NotTheEnglishServerText()
    {
        var text = InCulture("es-ES", () => AlertTextAfterFailedLogin(AccountLockedCode, AccountLockedServerMessage));

        text.Should().NotBe(
            AccountLockedServerMessage,
            "a Spanish UI must not show the API's English lockout sentence");
        text.Should().NotBe(
            InvalidCredentialsSpanish,
            "a locked account must be told it is locked, not that its password is wrong");
        text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void InEnglish_InvalidCredentials_StillShowsTheEnglishText()
    {
        var text = InCulture("en-US", () => AlertTextAfterFailedLogin(InvalidCredentialsCode, InvalidCredentialsServerMessage));

        text.Should().Be(InvalidCredentialsServerMessage);
    }

    [Fact]
    public void InEnglish_AccountLocked_StillShowsTheEnglishLockoutText()
    {
        var text = InCulture("en-US", () => AlertTextAfterFailedLogin(AccountLockedCode, AccountLockedServerMessage));

        text.Should().Be(AccountLockedServerMessage);
    }

    private static string InCulture(string culture, Func<string> act)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return act();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private string AlertTextAfterFailedLogin(string code, string serverMessage)
    {
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized(code, serverMessage, "LoginAsync")));

        var cut = RenderUnderTest<Login>(_ => { });
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.Find("input[autocomplete='current-password']").Input("Wr0ng!Passw0rd");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().ContainSingle(
            "a failed sign-in shows exactly one alert"));
        _auth.Verify(
            x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the password path must actually have been exercised");
        return cut.Find("[role='alert']").TextContent.Trim();
    }
}
