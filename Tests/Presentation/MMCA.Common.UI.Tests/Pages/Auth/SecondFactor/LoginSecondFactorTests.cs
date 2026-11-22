using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.SecondFactor;

/// <summary>
/// The code step on the shared sign-in page (design 2.2.6). It appears only when the server answers
/// <c>Authentication.TwoFactorRequired</c> (no <c>AddTwoFactorUI</c> is registered here): the code field
/// shows and takes focus, the credentials turn read-only, and the other ways in (sign-in link, external
/// providers) are hidden. The next submit carries the trimmed code; a wrong code keeps the step with its
/// message; "Use a different account" resets it. The <c>reason</c> query value shows the notice the
/// two-factor page sends the user back with, and any other value is ignored.
/// </summary>
public sealed class LoginSecondFactorTests : BunitTestBase
{
    private const string Email = "ada@example.com";
    private const string Password = "Str0ng!Passw0rd";
    private const string CodeField = "input[autocomplete='one-time-code']";
    private const string StartOverButton = "[data-testid='login-second-factor-start-over']";

    // SharedResource.resx values.
    private const string InvalidCodeText = "That code did not work. Check the time on your phone and try again, or use a recovery code.";
    private const string EnabledNotice = "Two-factor authentication is on. Sign in again with your password and the code from your app.";
    private const string RequiredNotice = "Sign in again with your password and the code from your app to continue.";

    private readonly Mock<IAuthUIService> _auth = new();
    private readonly Mock<IOAuthUISettings> _oauth = new();
    private readonly List<LoginRequest> _requests = [];

    public LoginSecondFactorTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        _oauth.SetupGet(s => s.GoogleEnabled).Returns(true);
        Services.AddSingleton(_oauth.Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
        Services.AddSingleton(Options.Create(new SignInSettings { OfferSignInLink = true }));
        Services.AddSingleton(new Mock<ISignInLinkUIService>().Object);

        // No code -> the second factor is required; any code -> the code did not verify.
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LoginRequest, CancellationToken>((request, _) => _requests.Add(request))
            .ReturnsAsync((LoginRequest request, CancellationToken _) => string.IsNullOrEmpty(request.TwoFactorCode)
                ? Refused(AuthErrorCodes.TwoFactorRequired, "A two-factor code is required.")
                : Refused(AuthErrorCodes.TwoFactorInvalid, "The two-factor code is invalid."));
    }

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void BeforeTheServerAsks_ThereIsNoCodeStep()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll(CodeField).Should().BeEmpty("the step appears only when the server answers that a second factor is required");
        cut.FindAll("[data-testid='login-second-factor-prompt']").Should().BeEmpty();
    }

    [Fact]
    public void TwoFactorRequired_ShowsTheCodeStep_WithTheCredentialsReadOnly_AndNoMessage()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        SubmitCredentials(cut);

        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());
        cut.Find("[data-testid='login-second-factor-prompt']").GetAttribute("role").Should().Be("status");
        cut.Find("input[autocomplete='email']").HasAttribute("readonly").Should().BeTrue();
        cut.Find("input[autocomplete='current-password']").HasAttribute("readonly").Should().BeTrue();
        cut.FindAll("[role='alert']").Should().BeEmpty("the password was right; the code is the expected next step, not a failure");
        cut.Find("button[type='submit']").TextContent.Should().Contain("Verify and sign in");
    }

    [Fact]
    public void TwoFactorRequired_MovesFocusToTheCodeField()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        SubmitCredentials(cut);

        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());
        var codeInput = cut.Find(CodeField).GetAttribute("blazor:elementReference");
        codeInput.Should().NotBeNullOrEmpty("precondition: the code input is a captured element");
        cut.WaitForAssertion(() => FocusCallsOn(codeInput!).Should().BeGreaterThan(0, "the code field takes focus when the step appears"));
    }

    [Fact]
    public void TwoFactorRequired_HidesTheOtherWaysIn()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        cut.FindAll("[data-testid='login-email-sign-in-link']").Should().ContainSingle("precondition: the host offers the sign-in link");
        cut.FindAll("button[aria-label='Sign in with Google']").Should().ContainSingle("precondition: a provider is configured");

        SubmitCredentials(cut);

        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());
        cut.FindAll("[data-testid='login-email-sign-in-link']").Should().BeEmpty();
        cut.FindAll("button[aria-label='Sign in with Google']").Should().BeEmpty();
    }

    [Fact]
    public void TheNextSubmit_SendsTheTrimmedCode_WithTheSameCredentials()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        SubmitCredentials(cut);
        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());

        cut.Find(CodeField).Input("  123456  ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => _requests.Should().HaveCount(2));
        _requests[0].TwoFactorCode.Should().BeNull("a first attempt never carries a code");
        _requests[1].TwoFactorCode.Should().Be("123456");
        _requests[1].Email.Should().Be(Email);
        _requests[1].Password.Should().Be(Password);
    }

    [Fact]
    public void AnEmptyCode_IsNotSent()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        SubmitCredentials(cut);
        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().ContainSingle());
        _requests.Should().ContainSingle("an empty code would only re-run the password check");
        cut.FindAll(CodeField).Should().ContainSingle();
    }

    [Fact]
    public void TwoFactorInvalid_KeepsTheStep_ShowsTheMessage_AndClearsTheCode()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        SubmitCredentials(cut);
        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());

        cut.Find(CodeField).Input("000000");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Trim().Should().Be(InvalidCodeText));
        cut.FindAll(CodeField).Should().ContainSingle("a wrong code keeps the user on the code step");
        cut.Find(CodeField).GetAttribute("value").Should().BeNullOrEmpty("the refused code is cleared for the next try");
        cut.Find("input[autocomplete='current-password']").HasAttribute("readonly").Should().BeTrue();
    }

    [Fact]
    public void StartOver_LeavesTheStep_AndClearsThePasswordAndTheCode()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        SubmitCredentials(cut);
        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());
        cut.Find(CodeField).Input("000000");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().ContainSingle());

        cut.Find(StartOverButton).Click();

        cut.FindAll(CodeField).Should().BeEmpty();
        cut.FindAll(StartOverButton).Should().BeEmpty();
        cut.FindAll("[role='alert']").Should().BeEmpty();
        cut.Find("input[autocomplete='email']").HasAttribute("readonly").Should().BeFalse();
        cut.Find("input[autocomplete='current-password']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.FindAll("[data-testid='login-email-sign-in-link']").Should().ContainSingle("the other ways in come back");
    }

    [Fact]
    public void AfterStartOver_TheNextSubmitCarriesNoCode()
    {
        var cut = RenderUnderTest<Login>(_ => { });
        SubmitCredentials(cut);
        cut.WaitForAssertion(() => cut.FindAll(CodeField).Should().ContainSingle());
        cut.Find(CodeField).Input("123456");
        cut.Find(StartOverButton).Click();

        SubmitCredentials(cut);

        cut.WaitForAssertion(() => _requests.Should().HaveCount(2));
        _requests[1].TwoFactorCode.Should().BeNull();
    }

    [Theory]
    [InlineData("two-factor-enabled", EnabledNotice)]
    [InlineData("two-factor-required", RequiredNotice)]
    public void TheReasonQuery_ShowsItsNotice_AsAStatus(string reason, string expected)
    {
        Navigation.NavigateTo($"/login?reason={reason}");

        var cut = RenderUnderTest<Login>(_ => { });

        var notice = cut.Find("[data-testid='login-reason-notice']");
        notice.TextContent.Trim().Should().Be(expected);
        notice.GetAttribute("role").Should().Be("status");
    }

    [Theory]
    [InlineData("/login?reason=something-else")]
    [InlineData("/login?reason=")]
    [InlineData("/login")]
    public void AnyOtherReason_ShowsNoNotice(string uri)
    {
        Navigation.NavigateTo(uri);

        var cut = RenderUnderTest<Login>(_ => { });

        cut.FindAll("[data-testid='login-reason-notice']").Should().BeEmpty();
    }

    private static Result<AuthenticationResponse> Refused(string code, string message) =>
        Result.Failure<AuthenticationResponse>(Error.Unauthorized(code, message, "LoginAsync"));

    private static void SubmitCredentials(IRenderedComponent<Login> cut)
    {
        cut.Find("input[autocomplete='email']").Input(Email);
        cut.Find("input[autocomplete='current-password']").Input(Password);
        cut.Find("form").Submit();
    }

    private int FocusCallsOn(string elementReferenceId) =>
        JSInterop.Invocations.Count(i =>
            i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase)
            && i.Arguments.OfType<Microsoft.AspNetCore.Components.ElementReference>().Any(r => r.Id == elementReferenceId));
}
