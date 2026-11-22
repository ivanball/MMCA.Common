using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.EmailConfirmation;

/// <summary>
/// The resend prompt on the sign-in page (design 2.4): it appears under the error alert only when the
/// refusal's single code is <c>Authentication.EmailNotConfirmed</c>, for the address as typed (trimmed),
/// and the next submit clears it. Any other refusal shows no prompt.
/// </summary>
public sealed class LoginResendPromptTests : BunitTestBase
{
    private const string Prompt = "[data-testid='email-confirmation-resend-prompt']";

    private readonly Mock<IAuthUIService> _auth = new();
    private readonly Mock<IEmailConfirmationUIService> _confirmation = new();

    public LoginResendPromptTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(_confirmation.Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
        _confirmation
            .Setup(x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    [Fact]
    public void EmailNotConfirmed_ShowsThePrompt_UnderTheAlert_ForTheTypedAddress()
    {
        RefuseWith(AuthErrorCodes.EmailNotConfirmed, "Confirm your email address before signing in.");
        var cut = RenderUnderTest<Login>(_ => { });

        Submit(cut, "  ada@example.com ");

        cut.WaitForAssertion(() => cut.FindAll(Prompt).Should().ContainSingle());
        cut.FindAll("[role='alert']").Should().ContainSingle("the refusal itself is still shown");
        cut.FindComponent<EmailConfirmationResendPrompt>().Instance.Email.Should().Be("ada@example.com");

        cut.Find("[data-testid='email-confirmation-resend']").Click();

        cut.WaitForAssertion(() => _confirmation.Verify(
            x => x.ResendEmailConfirmationAsync("ada@example.com", It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Theory]
    [InlineData("Auth.InvalidCredentials")]
    [InlineData("Auth.AccountLocked")]
    [InlineData("Authentication.TwoFactorInvalid")]
    public void AnyOtherRefusal_ShowsNoPrompt(string code)
    {
        RefuseWith(code, "Refused.");
        var cut = RenderUnderTest<Login>(_ => { });

        Submit(cut, "ada@example.com");

        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().NotBeEmpty());
        cut.FindAll(Prompt).Should().BeEmpty();
    }

    [Fact]
    public void TheNextSubmit_ClearsThePrompt()
    {
        RefuseWith(AuthErrorCodes.EmailNotConfirmed, "Confirm your email address before signing in.");
        var cut = RenderUnderTest<Login>(_ => { });
        Submit(cut, "ada@example.com");
        cut.WaitForAssertion(() => cut.FindAll(Prompt).Should().ContainSingle());

        RefuseWith("Auth.InvalidCredentials", "Invalid email or password.");
        Submit(cut, "grace@example.com");

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Should().Contain("Invalid email or password."));
        cut.FindAll(Prompt).Should().BeEmpty("the prompt belongs to the refusal it answered");
    }

    private void RefuseWith(string code, string message) =>
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(Error.Unauthorized(code, message, "LoginAsync")));

    private static void Submit(IRenderedComponent<Login> cut, string email)
    {
        cut.Find("input[autocomplete='email']").Input(email);
        cut.Find("input[autocomplete='current-password']").Input("Str0ng!Passw0rd");
        cut.Find("form").Submit();
    }
}
