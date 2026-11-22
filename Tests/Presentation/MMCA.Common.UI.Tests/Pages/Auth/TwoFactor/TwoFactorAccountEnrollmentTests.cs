using System.Web;
using AwesomeAssertions;
using Bunit;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Components.Sharing;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.TwoFactor;

/// <summary>
/// The account page from "off" to "on" (design 2.2.5): loading, the not-enrolled state (with the
/// required banner only for <c>?required=true</c>), Begin showing the QR image with its localized alt,
/// the grouped text key and the code field, a refused code kept inline, the recovery codes shown once
/// with Continue gated by the Saved checkbox, and Continue signing out before sending the user to sign in
/// again with a code. The recent-sign-in refusal offers the sign-out-and-sign-in-again step.
/// </summary>
public sealed class TwoFactorAccountEnrollmentTests : TwoFactorAccountTestsBase
{
    [Fact]
    public void WhileTheStatusLoads_ShowsTheLoadingState_ThenNotEnrolled()
    {
        var pending = new TaskCompletionSource<Result<TwoFactorStatusResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        TwoFactor.Setup(t => t.GetStatusAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);

        var cut = RenderPage();

        cut.Markup.Should().Contain("Loading two-factor settings");
        cut.FindAll("[data-testid='two-factor-begin']").Should().BeEmpty();

        pending.SetResult(Result.Success(new TwoFactorStatusResponse(false, 0)));

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-status']").TextContent.Trim()
            .Should().Be("Two-factor authentication is off."));
        cut.FindAll("[data-testid='two-factor-begin']").Should().ContainSingle();
    }

    [Fact]
    public void NotEnrolled_WithoutRequired_ShowsNoRequiredBanner()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));
        cut.FindAll("[data-testid='two-factor-required']").Should().BeEmpty();
    }

    [Fact]
    public void NotEnrolled_WithRequired_ShowsTheRequiredBannerAsAnAlert()
    {
        var cut = RenderPage("/profile/two-factor?required=true");

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-required']"));
        var banner = cut.Find("[data-testid='two-factor-required']");
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain("requires two-factor authentication");
    }

    [Fact]
    public void Begin_ShowsTheQrImageWithItsAlt_TheGroupedKey_AndTheCodeField()
    {
        var cut = RenderPage();

        Begin(cut);

        var qr = cut.FindComponent<QrCodeImage>();
        qr.Instance.Payload.Should().Be(ProvisioningUri, "the QR encodes the provisioning URI");
        var image = cut.Find("[data-testid='two-factor-enrollment'] img");
        image.GetAttribute("alt").Should().Be(QrAlt);
        cut.Find("[data-testid='two-factor-shared-key']").TextContent.Trim().Should().Be("JBSW Y3DP EHPK 3PXP JBSW Y3DP");
        cut.Find("[data-testid='two-factor-shared-key']").ParentElement!.GetAttribute("aria-label").Should().Be("Setup key");
        var code = cut.Find("[data-testid='two-factor-enrollment'] input[autocomplete='one-time-code']");
        code.GetAttribute("inputmode").Should().Be("numeric");
        cut.FindAll("[data-testid='two-factor-begin']").Should().BeEmpty();
    }

    [Fact]
    public void Confirm_SendsTheTrimmedCode_AndShowsEveryRecoveryCodeOnce()
    {
        var cut = RenderPage();
        Begin(cut);

        ConfirmWith(cut, " 123456 ");

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-code-list']"));
        TwoFactor.Verify(t => t.ConfirmEnrollmentAsync("123456", It.IsAny<CancellationToken>()), Times.Once());
        var listed = cut.FindAll("[data-testid='two-factor-recovery-code-list'] li").Select(li => li.TextContent.Trim()).ToList();
        listed.Should().Equal(Codes);
        foreach (var recoveryCode in Codes)
        {
            CountOf(cut.Markup, recoveryCode).Should().Be(1, "each recovery code is shown exactly once");
        }

        cut.FindAll("[data-testid='two-factor-shared-key']").Should().BeEmpty("the secret is gone once the codes are shown");
        cut.Markup.Should().NotContain(SharedKey);
    }

    [Fact]
    public void Continue_IsDisabledUntilTheSavedCheckboxIsTicked()
    {
        var cut = RenderPage();
        Begin(cut);
        ConfirmWith(cut, "123456");
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-codes-continue']"));

        cut.Find("[data-testid='two-factor-recovery-codes-continue']").HasAttribute("disabled").Should().BeTrue();

        CheckSaved(cut);

        cut.Find("[data-testid='two-factor-recovery-codes-continue']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ContinueAfterConfirm_SignsOutFirst_ThenSendsTheUserToSignInWithAFullLoad()
    {
        var navigationsAtLogout = -1;
        Auth.Setup(a => a.LogoutAsync()).Callback(() => navigationsAtLogout = Navigation.History.Count).Returns(Task.CompletedTask);
        var cut = RenderPage();
        var navigationsBefore = Navigation.History.Count;
        Begin(cut);
        ConfirmWith(cut, "123456");
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-codes-continue']"));
        CheckSaved(cut);

        cut.Find("[data-testid='two-factor-recovery-codes-continue']").Click();

        cut.WaitForAssertion(() => Auth.Verify(a => a.LogoutAsync(), Times.Once()));
        navigationsAtLogout.Should().Be(navigationsBefore, "the session ends before the page leaves");
        var last = Navigation.History.First();
        last.Uri.Should().EndWith("/login?reason=two-factor-enabled");
        last.Options.ForceLoad.Should().BeTrue("the new sign-in must start from a fresh load");
    }

    [Fact]
    public void ContinueAfterConfirm_CarriesTheReturnUrl()
    {
        var cut = RenderPage("/profile/two-factor?required=true&returnUrl=%2Fadmin%2Fusers");
        Begin(cut);
        ConfirmWith(cut, "123456");
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recovery-codes-continue']"));
        CheckSaved(cut);

        cut.Find("[data-testid='two-factor-recovery-codes-continue']").Click();

        cut.WaitForAssertion(() => Auth.Verify(a => a.LogoutAsync(), Times.Once()));
        var target = new Uri(new Uri("http://localhost/"), Navigation.History.First().Uri);
        target.AbsolutePath.Should().Be("/login");
        var query = HttpUtility.ParseQueryString(target.Query);
        query["reason"].Should().Be("two-factor-enabled");
        query["returnUrl"].Should().Be("/admin/users");
    }

    [Fact]
    public void ARefusedCode_KeepsThePanel_AndShowsTheLocalizedMessageInline()
    {
        TwoFactor
            .Setup(t => t.ConfirmEnrollmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<TwoFactorRecoveryCodesResponse>(AuthErrorCodes.TwoFactorInvalid, "The two-factor code is invalid."));
        var cut = RenderPage();
        Begin(cut);

        ConfirmWith(cut, "000000");

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-enrollment-error']"));
        var error = cut.Find("[data-testid='two-factor-enrollment-error']");
        error.TextContent.Trim().Should().Be(InvalidCodeText);
        error.GetAttribute("role").Should().Be("alert");
        cut.FindAll("[data-testid='two-factor-enrollment']").Should().ContainSingle("a wrong code keeps the user on the set-up panel");
        cut.FindAll("[data-testid='two-factor-recovery-code-list']").Should().BeEmpty();
    }

    [Fact]
    public void Cancel_ReturnsToNotEnrolled()
    {
        var cut = RenderPage();
        Begin(cut);

        cut.Find("[data-testid='two-factor-enrollment-cancel']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));
        cut.FindAll("[data-testid='two-factor-enrollment']").Should().BeEmpty();
        TwoFactor.Verify(t => t.ConfirmEnrollmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void ARecentSignInRefusalOnBegin_OffersToSignOutAndSignInAgain()
    {
        TwoFactor
            .Setup(t => t.BeginEnrollmentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<TwoFactorSetupResponse>(RecentSignInCode, "Sign in again first."));
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));

        cut.Find("[data-testid='two-factor-begin']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recent-sign-in']"));
        cut.Find("[data-testid='two-factor-recent-sign-in']").TextContent
            .Should().Contain("For your security, sign in again before changing two-factor authentication.");

        cut.Find("[data-testid='two-factor-sign-in-again']").Click();

        cut.WaitForAssertion(() => Auth.Verify(a => a.LogoutAsync(), Times.Once()));
        var last = Navigation.History.First();
        last.Uri.Should().EndWith("/login?reason=two-factor-required");
        last.Options.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public void ARecentSignInRefusalOnConfirm_LeavesThePanel_AndOffersTheSignInAgainStep()
    {
        TwoFactor
            .Setup(t => t.ConfirmEnrollmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<TwoFactorRecoveryCodesResponse>(RecentSignInCode, "Sign in again first."));
        var cut = RenderPage();
        Begin(cut);

        ConfirmWith(cut, "123456");

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-recent-sign-in']"));
        cut.FindAll("[data-testid='two-factor-enrollment']").Should().BeEmpty();
        cut.Markup.Should().NotContain(SharedKey);
    }

    [Fact]
    public void ALoadFailure_ShowsTheErrorSummaryAndRetry_AndRetryLoadsTheStatus()
    {
        TwoFactor
            .SetupSequence(t => t.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<TwoFactorStatusResponse>(Error.Failure("TwoFactor.StatusUnavailable", "The status could not be read.")))
            .ReturnsAsync(Result.Success(new TwoFactorStatusResponse(false, 0)));
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-retry']"));
        cut.Markup.Should().Contain("Two-factor settings could not be loaded.");
        cut.Markup.Should().Contain("The status could not be read.", "the ErrorSummary lists the failure");

        cut.Find("[data-testid='two-factor-retry']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));
        cut.FindAll("[data-testid='two-factor-retry']").Should().BeEmpty();
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
