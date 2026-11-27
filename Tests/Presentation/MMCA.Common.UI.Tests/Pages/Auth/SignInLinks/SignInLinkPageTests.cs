using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI.Pages;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.SignInLinks;

/// <summary>
/// Runs the shared <see cref="SignInLinkPageTestsBase"/> facts over the framework's own
/// <c>/auth/sign-in-link</c> page, so the base the consumers subclass is exercised in this repo's CI.
/// </summary>
public sealed class SignInLinkPageTests : SignInLinkPageTestsBase
{
    // Run 19 N1: type=email refuses an internationalized local part in the browser, so the fresh-link
    // form's email is a text input that still asks for the email keyboard.
    [Fact]
    public void FreshLinkEmailField_IsATextInputWithTheEmailKeyboard()
    {
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}");

        cut.WaitForAssertion(() => cut.Find("input[data-testid=sign-in-link-email]"));
        var input = cut.Find("input[data-testid=sign-in-link-email]");
        input.GetAttribute("type").Should().Be("text");
        input.GetAttribute("inputmode").Should().Be("email");
    }

    // U-48 (ADC local test run 21): on MAUI a sign-in link opened while this page is already showing
    // is a same-route navigation with a new fragment; the page read its fragment on first render only,
    // so the second link (the in-page "Email me a new link" flow) was ignored.
    [Fact]
    public void ASecondLinkOpenedOnTheOpenPage_IsTheOneContinueRedeems()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo(PagePath);
        var cut = RenderPage(Fragment("tok-A"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-continue]"));

        navigation.NavigateTo($"{PagePath}#{Fragment("tok-B")}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-continue]"));
        cut.Find("[data-testid=sign-in-link-continue]").Click();

        cut.WaitForAssertion(() => navigation.Uri.Should().EndWith(LandingPath));
        SignInLinks.Verify(x => x.RedeemSignInLinkAsync(Email, "tok-B", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once());
        SignInLinks.Verify(x => x.RedeemSignInLinkAsync(It.IsAny<string>(), "tok-A", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // The same link opened again after Continue posted it must not offer a second POST of a spent token.
    [Fact]
    public void ALinkWhoseTokenWasAlreadyPosted_IsNotOfferedAgain()
    {
        SignInLinks
            .Setup(x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Authentication.InvalidSignInLink", "Invalid link.", "Test")));
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo(PagePath);
        var cut = RenderPage(Fragment("tok-A"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-continue]"));
        cut.Find("[data-testid=sign-in-link-continue]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-error]"));

        navigation.NavigateTo($"{PagePath}#{Fragment("tok-A")}");

        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-request]"));
        cut.FindAll("[data-testid=sign-in-link-continue]").Should().BeEmpty("a spent token is not offered again");
        SignInLinks.Verify(x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    private const string PagePath = "/auth/sign-in-link";

    private string Fragment(string token) => $"email={Uri.EscapeDataString(Email)}&token={token}";
}
