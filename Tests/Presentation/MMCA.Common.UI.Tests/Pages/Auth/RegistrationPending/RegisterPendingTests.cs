using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Navigation;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.RegistrationPending;

/// <summary>
/// The Register page behind the email gate (design 2.3): a "confirmation pending" success opens no
/// session, so the page stays where it is and shows the check-your-email state with the address, the
/// resend prompt and a way to sign in (the address handed over in history state, never the URL). A
/// success that opened a session still lands on the home page with a full load.
/// </summary>
public sealed class RegisterPendingTests : BunitTestBase
{
    private const string Email = "ada@example.com";
    private const string Pending = "[data-testid='register-pending']";

    private readonly Mock<IAuthUIService> _auth = new();

    public RegisterPendingTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(new Mock<IEmailConfirmationUIService>().Object);
    }

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void APendingResult_ShowsCheckYourEmail_WithTheAddress_AndDoesNotNavigate()
    {
        RegistrationReturns(Result.Success(RegistrationOutcome.ConfirmationPending));
        Navigation.NavigateTo("/register");
        var historyBefore = Navigation.History.Count;
        var cut = RenderUnderTest<Register>(_ => { });

        FillAndSubmit(cut, Email);

        cut.WaitForAssertion(() => cut.FindAll(Pending).Should().ContainSingle());
        cut.Find($"{Pending} h2").TextContent.Trim().Should().Be("Check your email");
        var message = cut.Find($"{Pending} [role='status']");
        message.TextContent.Should().Contain(Email);
        message.GetAttribute("aria-live").Should().Be("polite");
        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/register", "no session was opened, so there is nowhere signed-in to go");
        Navigation.History.Count.Should().Be(historyBefore);
    }

    [Fact]
    public void APendingResult_ReplacesTheForm_AndOffersTheResendPromptForTheAddress()
    {
        RegistrationReturns(Result.Success(RegistrationOutcome.ConfirmationPending));
        var cut = RenderUnderTest<Register>(_ => { });

        FillAndSubmit(cut, Email);

        cut.WaitForAssertion(() => cut.FindAll(Pending).Should().ContainSingle());
        cut.FindAll("form").Should().BeEmpty("the form is not shown again");
        cut.FindAll("input[autocomplete='new-password']").Should().BeEmpty();
        cut.FindComponent<EmailConfirmationResendPrompt>().Instance.Email.Should().Be(Email);
    }

    [Fact]
    public void GoToSignIn_OpensSignInWithTheAddressInHistoryState()
    {
        RegistrationReturns(Result.Success(RegistrationOutcome.ConfirmationPending));
        var cut = RenderUnderTest<Register>(_ => { });
        FillAndSubmit(cut, Email);
        cut.WaitForAssertion(() => cut.FindAll(Pending).Should().ContainSingle());

        cut.Find("[data-testid='register-pending-go-to-sign-in']").Click();

        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/login");
        Navigation.Uri.Should().NotContain(Email, "the address stays out of the URL");
        AuthEmailPrefill.Read(Navigation, out var existingAccount).Should().Be(Email);
        existingAccount.Should().BeFalse("the account is new; there is no existing-account notice");
    }

    [Fact]
    public void ASessionResult_StillNavigatesHome_WithAFullLoad()
    {
        RegistrationReturns(Result.Success(new AuthenticationResponse(
            "access-token", "refresh-token", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc))));
        var cut = RenderUnderTest<Register>(_ => { });

        FillAndSubmit(cut, Email);

        cut.WaitForAssertion(() => Navigation.History.Should().NotBeEmpty());
        var last = Navigation.History.First();
        new Uri(last.Uri, UriKind.RelativeOrAbsolute).ToString().Should().EndWith("/");
        last.Options.ForceLoad.Should().BeTrue("a real session reloads the app signed in");
        cut.FindAll(Pending).Should().BeEmpty();
    }

    private void RegistrationReturns(Result<AuthenticationResponse> result) =>
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static void FillAndSubmit(IRenderedComponent<Register> cut, string email)
    {
        cut.Find("input[autocomplete='given-name']").Input("Ada");
        cut.Find("input[autocomplete='family-name']").Input("Lovelace");
        cut.Find("input[autocomplete='email']").Input(email);
        cut.FindAll("input[autocomplete='new-password']")[0].Input("Str0ng!Passw0rd");
        cut.FindAll("input[autocomplete='new-password']")[1].Input("Str0ng!Passw0rd");
        cut.ClickButtonByText("Create Account");
    }
}
