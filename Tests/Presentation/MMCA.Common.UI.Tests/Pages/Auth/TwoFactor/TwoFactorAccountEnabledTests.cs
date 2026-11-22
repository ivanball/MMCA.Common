using AwesomeAssertions;
using Bunit;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.TwoFactor;

/// <summary>
/// The account page while two-factor is on (design 2.2.5): the status with the remaining recovery
/// codes, regenerating codes (shown once, then the status again, no sign-out), turning it off behind a
/// confirmation (toast, then not enrolled), copying the codes, and, when the user was sent here because a
/// role requires the factor but this session was opened without it, the sign-in-again banner.
/// </summary>
public sealed class TwoFactorAccountEnabledTests : TwoFactorAccountTestsBase
{
    private static readonly IReadOnlyList<string> NewCodes = ["NNNN-1111", "PPPP-2222"];

    public TwoFactorAccountEnabledTests()
    {
        StatusIs(enabled: true, remaining: 7);
        TwoFactor
            .Setup(t => t.RegenerateRecoveryCodesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TwoFactorRecoveryCodesResponse(NewCodes)));
        TwoFactor
            .Setup(t => t.DisableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    [Fact]
    public void Enabled_ShowsTheStatusAndTheRemainingCount()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-codes-remaining']"));
        cut.Find("[data-testid='two-factor-status']").TextContent.Trim().Should().Be("Two-factor authentication is on.");
        cut.Find("[data-testid='two-factor-codes-remaining']").TextContent.Trim().Should().Be("Recovery codes left: 7");
        cut.FindAll("[data-testid='two-factor-begin']").Should().BeEmpty();
    }

    [Fact]
    public void Regenerate_ShowsTheNewCodes_AndContinueReloadsTheStatus_WithoutSigningOut()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-regenerate']"));
        EnterActionCode(cut, " 654321 ");

        cut.Find("[data-testid='two-factor-regenerate']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-code-list']"));
        TwoFactor.Verify(t => t.RegenerateRecoveryCodesAsync("654321", It.IsAny<CancellationToken>()), Times.Once());
        cut.FindAll("[data-testid='two-factor-recovery-code-list'] li").Select(li => li.TextContent.Trim()).Should().Equal(NewCodes);

        cut.Find("[data-testid='two-factor-recovery-codes'] input[type='checkbox']").Change(true);
        cut.Find("[data-testid='two-factor-recovery-codes-continue']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-codes-remaining']"));
        TwoFactor.Verify(t => t.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Auth.Verify(a => a.LogoutAsync(), Times.Never(), "regenerating codes keeps the session");
        cut.Markup.Should().NotContain(NewCodes[0], "the codes are shown once and then dropped");
    }

    [Fact]
    public void Disable_AsksFirst_ThenTurnsItOff_WithAToast_AndShowsNotEnrolled()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-disable']"));
        EnterActionCode(cut, "111222");

        cut.Find("[data-testid='two-factor-disable']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));
        Dialogs.Verify(d => d.ConfirmAsync(
            "Turn off two-factor authentication?",
            "Your account will be protected by your password only.",
            It.IsAny<string>(),
            It.IsAny<string>(),
            true), Times.Once());
        TwoFactor.Verify(t => t.DisableAsync("111222", It.IsAny<CancellationToken>()), Times.Once());
        Toast.Verify(t => t.Success("Two-factor authentication is off."), Times.Once());
        cut.Find("[data-testid='two-factor-status']").TextContent.Trim().Should().Be("Two-factor authentication is off.");
    }

    [Fact]
    public void Disable_Declined_TurnsNothingOff()
    {
        Dialogs
            .Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(false);
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-disable']"));
        EnterActionCode(cut, "111222");

        cut.Find("[data-testid='two-factor-disable']").Click();

        cut.WaitForAssertion(() => Dialogs.Verify(
            d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once()));
        TwoFactor.Verify(t => t.DisableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        cut.FindAll("[data-testid='two-factor-codes-remaining']").Should().ContainSingle("the page stays on the enabled state");
    }

    [Fact]
    public void ARefusedActionCode_ShowsTheLocalizedAlert_AndKeepsTheEnabledState()
    {
        TwoFactor
            .Setup(t => t.RegenerateRecoveryCodesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<TwoFactorRecoveryCodesResponse>(AuthErrorCodes.TwoFactorInvalid, "The two-factor code is invalid."));
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-regenerate']"));
        EnterActionCode(cut, "000000");

        cut.Find("[data-testid='two-factor-regenerate']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-action-error']").TextContent.Trim().Should().Be(InvalidCodeText));
        cut.Find("[data-testid='two-factor-action-error']").GetAttribute("role").Should().Be("alert");
        cut.FindAll("[data-testid='two-factor-codes-remaining']").Should().ContainSingle();
    }

    [Fact]
    public void RequiredAndThisSessionHasNoSecondFactor_ShowsTheSignInAgainBanner()
    {
        var cut = RenderPage("/profile/two-factor?required=true", withMultiFactor: false);

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-required-sign-in-again']"));
        var banner = cut.Find("[data-testid='two-factor-required-sign-in-again']");
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain("This session was started without your authenticator code.");

        banner.QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Auth.Verify(a => a.LogoutAsync(), Times.Once()));
        Navigation.History.First().Uri.Should().Contain("/login?reason=two-factor-required");
        Navigation.History.First().Options.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public void RequiredButThisSessionUsedItsSecondFactor_ShowsNoBanner()
    {
        var cut = RenderPage("/profile/two-factor?required=true", withMultiFactor: true);

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-codes-remaining']"));
        cut.FindAll("[data-testid='two-factor-required-sign-in-again']").Should().BeEmpty();
    }

    [Fact]
    public void NotRequired_ShowsNoBanner_EvenWithoutTheSecondFactor()
    {
        var cut = RenderPage(withMultiFactor: false);

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-codes-remaining']"));
        cut.FindAll("[data-testid='two-factor-required-sign-in-again']").Should().BeEmpty();
    }

    [Fact]
    public void Copy_PutsEveryCodeOnTheClipboard_AndConfirmsWithAToast()
    {
        Clipboard.Setup(c => c.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-regenerate']"));
        EnterActionCode(cut, "654321");
        cut.Find("[data-testid='two-factor-regenerate']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-codes-copy']"));

        cut.Find("[data-testid='two-factor-recovery-codes-copy']").Click();

        cut.WaitForAssertion(() => Clipboard.Verify(
            c => c.SetTextAsync(It.Is<string>(text => NewCodes.All(text.Contains)), It.IsAny<CancellationToken>()),
            Times.Once()));
        Toast.Verify(t => t.Success("Recovery codes copied."), Times.Once());
    }

    [Fact]
    public void ACopyThatFails_SaysSoWithAToast()
    {
        Clipboard.Setup(c => c.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-regenerate']"));
        EnterActionCode(cut, "654321");
        cut.Find("[data-testid='two-factor-regenerate']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-codes-copy']"));

        cut.Find("[data-testid='two-factor-recovery-codes-copy']").Click();

        cut.WaitForAssertion(() => Toast.Verify(t => t.Error(It.IsAny<string>()), Times.Once()));
        Toast.Verify(t => t.Success(It.IsAny<string>()), Times.Never());
    }

    private static void EnterActionCode(IRenderedComponent<UI.Pages.Auth.TwoFactor.TwoFactorAccount> cut, string code) =>
        cut.Find("input[autocomplete='one-time-code']").Input(code);
}
