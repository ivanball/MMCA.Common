using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Auth.TwoFactor;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.TwoFactor;

/// <summary>
/// Shared harness for the two-factor account page tests: the page's client, the sign-out, the
/// confirmation dialog (confirms by default), the toasts and the clipboard are doubles; the page is
/// rendered signed in, without the <c>mfa</c> claim unless a test asks for it.
/// </summary>
public abstract class TwoFactorAccountTestsBase : BunitTestBase
{
    protected const string SharedKey = "JBSWY3DPEHPK3PXPJBSWY3DP";
    protected const string ProvisioningUri = "otpauth://totp/App:ada%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DP&issuer=App";
    protected const string RecentSignInCode = "Authentication.TwoFactorRecentSignInRequired";

    // SharedResource.resx values.
    protected const string QrAlt = "QR code that adds this account to an authenticator app. The same key is shown as text below.";
    protected const string InvalidCodeText = "That code did not work. Check the time on your phone and try again, or use a recovery code.";

    protected static readonly IReadOnlyList<string> Codes = ["AAAA-1111", "BBBB-2222", "CCCC-3333"];

    protected TwoFactorAccountTestsBase()
    {
        Services.AddSingleton(TwoFactor.Object);
        Services.AddSingleton(Auth.Object);
        Services.AddSingleton(Dialogs.Object);
        Services.AddSingleton(Toast.Object);
        Services.AddSingleton(Clipboard.Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();

        Dialogs
            .Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(true);
        Auth.Setup(a => a.LogoutAsync()).Returns(Task.CompletedTask);
        StatusIs(enabled: false);
        TwoFactor
            .Setup(t => t.BeginEnrollmentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TwoFactorSetupResponse(SharedKey, ProvisioningUri)));
        TwoFactor
            .Setup(t => t.ConfirmEnrollmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TwoFactorRecoveryCodesResponse(Codes)));
    }

    protected Mock<ITwoFactorUIService> TwoFactor { get; } = new();

    protected Mock<IAuthUIService> Auth { get; } = new();

    protected Mock<IAppDialogService> Dialogs { get; } = new();

    protected Mock<IToastService> Toast { get; } = new();

    protected Mock<IClipboardService> Clipboard { get; } = new();

    protected BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    protected static Result<T> Refused<T>(string code, string message) =>
        Result.Failure<T>(Error.Unauthorized(code, message));

    protected void StatusIs(bool enabled, int remaining = 0) =>
        TwoFactor
            .Setup(t => t.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TwoFactorStatusResponse(enabled, remaining)));

    protected IRenderedComponent<TwoFactorAccount> RenderPage(string uri = "/profile/two-factor", bool withMultiFactor = false)
    {
        Navigation.NavigateTo(uri);
        var principal = TestPrincipal.AuthenticatedUser();
        if (withMultiFactor)
        {
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AuthClaimTypes.MultiFactor, "otp"));
        }

        return RenderAs<TwoFactorAccount>(principal, _ => { });
    }

    protected static void Begin(IRenderedComponent<TwoFactorAccount> cut)
    {
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-begin']"));
        cut.Find("[data-testid='two-factor-begin']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='two-factor-enrollment']"));
    }

    protected static void ConfirmWith(IRenderedComponent<TwoFactorAccount> cut, string code)
    {
        cut.Find("[data-testid='two-factor-enrollment'] input[autocomplete='one-time-code']").Input(code);
        cut.Find("[data-testid='two-factor-enrollment-confirm']").Click();
    }

    protected static void CheckSaved(IRenderedComponent<TwoFactorAccount> cut) =>
        cut.Find("[data-testid='two-factor-recovery-codes'] input[type='checkbox']").Change(true);
}
