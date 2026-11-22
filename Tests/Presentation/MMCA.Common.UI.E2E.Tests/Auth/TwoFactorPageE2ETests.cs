using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The shared two-factor account page in a real browser (design 5.5), against the gallery's canned
/// client (off, set up, recovery codes, on; <c>000000</c> is refused): Begin shows a visible QR image
/// with a non-empty alt and the text key; the recovery codes are listed; Continue is enabled only after
/// the "saved" checkbox; the whole enrollment runs from the keyboard; the states reached by clicks pass
/// the axe-core WCAG 2.1 AA scan (the states reached by URL are in <see cref="Accessibility.AxeSweepE2ETests"/>);
/// and at phone width the page does not scroll sideways.
/// </summary>
public sealed partial class TwoFactorPageE2ETests : GalleryAxeTestBase
{
    private const string Route = "/profile/two-factor";
    private const int PhoneWidth = 390;
    private const int PhoneHeight = 844;

    public TwoFactorPageE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task Begin_ShowsAVisibleQrImageWithAnAlt_AndTheTextKey()
    {
        await OpenSignedInAsync(Route);

        await Page.GetByTestId("two-factor-begin").ClickAsync();

        var qr = Page.GetByTestId("two-factor-enrollment").Locator("img");
        await Expect(qr).ToBeVisibleAsync();
        await Expect(qr).ToHaveAttributeAsync("alt", NonBlank);
        await Expect(Page.GetByTestId("two-factor-shared-key")).ToHaveTextAsync("JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP");
        await Expect(Page.GetByTestId("two-factor-enrollment-code")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Enrolling_HasNoWcag21AaViolations()
    {
        await OpenSignedInAsync(Route);
        await Page.GetByTestId("two-factor-begin").ClickAsync();
        await Expect(Page.GetByTestId("two-factor-shared-key")).ToBeVisibleAsync();

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task AWrongCode_ShowsTheInlineAlert_AndHasNoWcag21AaViolations()
    {
        await OpenSignedInAsync(Route);
        await Page.GetByTestId("two-factor-begin").ClickAsync();
        await Page.GetByTestId("two-factor-enrollment-code").FillAndVerifyAsync("000000");

        await Page.GetByTestId("two-factor-enrollment-confirm").ClickAsync();

        await Expect(Page.GetByTestId("two-factor-enrollment-error")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("two-factor-enrollment")).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task RecoveryCodes_AreListed_AndContinueIsEnabledOnlyAfterTheCheckbox()
    {
        await OpenSignedInAsync(Route);
        await Page.GetByTestId("two-factor-begin").ClickAsync();
        await Page.GetByTestId("two-factor-enrollment-code").FillAndVerifyAsync("123456");

        await Page.GetByTestId("two-factor-enrollment-confirm").ClickAsync();

        var list = Page.GetByTestId("two-factor-recovery-code-list");
        await Expect(list).ToBeVisibleAsync();
        await Expect(list.Locator("li")).ToHaveCountAsync(10);
        await Expect(list.Locator("li").First).ToHaveTextAsync("AAAA-1111");
        var continueButton = Page.GetByTestId("two-factor-recovery-codes-continue");
        await Expect(continueButton).ToBeDisabledAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);

        await SavedCheckbox().SetCheckedAsync(true, new() { Force = true });

        await Expect(continueButton).ToBeEnabledAsync();
    }

    [Fact]
    public async Task TheWholeEnrollment_RunsFromTheKeyboard()
    {
        await OpenSignedInAsync(Route);
        var begin = Page.GetByTestId("two-factor-begin");
        await Expect(begin).ToBeVisibleAsync();
        await begin.FocusAsync();

        await Page.Keyboard.PressAsync("Enter");
        var code = Page.GetByTestId("two-factor-enrollment-code");
        await Expect(code).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("123456");
        await Expect(code).ToHaveValueAsync("123456");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByTestId("two-factor-enrollment-confirm")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");

        var heading = Page.GetByRole(AriaRole.Heading, new() { Name = "Save your recovery codes" });
        await Expect(heading).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByTestId("two-factor-recovery-codes-copy")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(SavedCheckbox()).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(SavedCheckbox()).ToBeCheckedAsync();
        await Page.Keyboard.PressAsync("Tab");
        var continueButton = Page.GetByTestId("two-factor-recovery-codes-continue");
        await Expect(continueButton).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page).ToHaveURLAsync($"{BaseUrl}/login?reason=two-factor-enabled");
        await Expect(Page.GetByTestId("login-reason-notice")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Enabled_Regenerate_ShowsNewCodes_AndContinueReturnsToTheStatus()
    {
        await OpenSignedInAsync($"{Route}?gallery-state=on");
        await Expect(Page.GetByTestId("two-factor-codes-remaining")).ToHaveTextAsync("Recovery codes left: 7");
        await Page.GetByTestId("two-factor-action-code").FillAndVerifyAsync("123456");

        await Page.GetByTestId("two-factor-regenerate").ClickAsync();

        await Expect(Page.GetByTestId("two-factor-recovery-code-list").Locator("li").First).ToHaveTextAsync("LLLL-1111");
        await SavedCheckbox().SetCheckedAsync(true, new() { Force = true });
        await Page.GetByTestId("two-factor-recovery-codes-continue").ClickAsync();
        await Expect(Page.GetByTestId("two-factor-codes-remaining")).ToHaveTextAsync("Recovery codes left: 10");
    }

    [Fact]
    public async Task Enabled_WrongActionCode_ShowsTheAlert_AndHasNoWcag21AaViolations()
    {
        await OpenSignedInAsync($"{Route}?gallery-state=on");
        await Page.GetByTestId("two-factor-action-code").FillAndVerifyAsync("000000");

        await Page.GetByTestId("two-factor-regenerate").ClickAsync();

        await Expect(Page.GetByTestId("two-factor-action-error")).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task Enabled_Disable_AsksFirst_ThenShowsNotEnrolled()
    {
        await OpenSignedInAsync($"{Route}?gallery-state=on");
        await Page.GetByTestId("two-factor-action-code").FillAndVerifyAsync("123456");

        await Page.GetByTestId("two-factor-disable").ClickAsync();

        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Turn off two-factor authentication" }).ClickAsync();
        await Expect(Page.GetByTestId("two-factor-begin")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("two-factor-status")).ToHaveTextAsync("Two-factor authentication is off.");
    }

    [Fact]
    public async Task LoadFailure_Retry_LoadsTheStatus()
    {
        await OpenSignedInAsync($"{Route}?gallery-state=fail");
        await Expect(Page.GetByTestId("two-factor-retry")).ToBeVisibleAsync();

        await Page.GetByTestId("two-factor-retry").ClickAsync();

        await Expect(Page.GetByTestId("two-factor-begin")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(Route)]
    [InlineData(Route + "?gallery-state=on&required=true")]
    public async Task AtPhoneWidth_ThePageDoesNotScrollSideways(string path)
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await OpenSignedInAsync(path);
        await Expect(Page.GetByTestId("two-factor-status")).ToBeVisibleAsync();

        await AssertNoSidewaysScrollAsync(path);
    }

    [Fact]
    public async Task AtPhoneWidth_TheSetUpPanelAndTheCodesDoNotScrollSideways()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await OpenSignedInAsync(Route);
        await Page.GetByTestId("two-factor-begin").ClickAsync();
        await Expect(Page.GetByTestId("two-factor-shared-key")).ToBeVisibleAsync();
        await AssertNoSidewaysScrollAsync("the set-up panel");

        await Page.GetByTestId("two-factor-enrollment-code").FillAndVerifyAsync("123456");
        await Page.GetByTestId("two-factor-enrollment-confirm").ClickAsync();
        await Expect(Page.GetByTestId("two-factor-recovery-code-list")).ToBeVisibleAsync();
        await AssertNoSidewaysScrollAsync("the recovery codes");
    }

    [GeneratedRegex(@"\S", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonBlank { get; }

    private ILocator SavedCheckbox() =>
        Page.GetByRole(AriaRole.Checkbox, new() { Name = "I have saved these codes" });

    private async Task AssertNoSidewaysScrollAsync(string what)
    {
        var overflow = await Page.EvaluateAsync<int>(
            "() => Math.max(0, document.scrollingElement.scrollWidth - document.scrollingElement.clientWidth)");
        Assert.True(overflow <= 1,
            string.Create(CultureInfo.InvariantCulture, $"{what} scrolls sideways by {overflow}px at {PhoneWidth}px"));
    }

    // The page carries a real policy; the gallery's cookie-toggled fake scheme signs it in (as in
    // SessionsPageE2ETests).
    private async Task OpenSignedInAsync(string path)
    {
        await Page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = "gallery_auth",
                Value = "1",
                Url = BaseUrl,
            },
        ]);
        await Page.GotoAndWaitForBlazorAsync(path);
    }
}
