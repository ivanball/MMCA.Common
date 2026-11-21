using AwesomeAssertions;
using Bunit;
using MMCA.Common.Testing.UI.Pages;

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
}
