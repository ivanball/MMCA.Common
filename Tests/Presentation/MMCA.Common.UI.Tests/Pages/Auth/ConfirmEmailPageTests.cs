using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI.Pages;
using MMCA.Common.UI.Services.Capabilities;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// Runs the shared <see cref="ConfirmEmailPageTestsBase"/> facts over the framework's own
/// <c>/confirm-email</c> page, so the base the consumers subclass is exercised in this repo's CI.
/// The facts added here drive the URL-fragment path the base leaves out: the capabilities module is
/// registered and its <c>locationHashTake</c> export answers with the fragment the emailed link carried.
/// </summary>
public sealed class ConfirmEmailPageTests : ConfirmEmailPageTestsBase
{
    private const string CapabilitiesModulePath = "./_content/MMCA.Common.UI/capabilities-interop.js";

    // Run 19 N1: type=email refuses an internationalized local part in the browser, so the resend
    // form's email is a text input that still asks for the email keyboard.
    [Fact]
    public void EmailField_IsATextInputWithTheEmailKeyboard()
    {
        var cut = RenderPage(Email, token: null);

        cut.WaitForAssertion(() => cut.Find("input[data-testid=confirm-email-address]"));
        var input = cut.Find("input[data-testid=confirm-email-address]");
        input.GetAttribute("type").Should().Be("text");
        input.GetAttribute("inputmode").Should().Be("email");
    }

    [Fact]
    public void Fragment_CarryingBothValues_IsRedeemedDecoded()
    {
        UseFragment("email=grace%40example.com&token=tok%2B1%3D");

        var cut = RenderPage(email: null, token: null);

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-success]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync("grace@example.com", "tok+1=", It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void Fragment_SkipsMalformedAndUnknownPairs_AndMatchesNamesExactly()
    {
        UseFragment("=orphan&novalue&Email=wrong%40example.com&lang=en&email=grace%40example.com&token=t-9");

        var cut = RenderPage(email: null, token: null);

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-success]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync("grace@example.com", "t-9", It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void Fragment_OnlyFillsEmptyFields_SoTheQueryAndTheFirstPairWin()
    {
        UseFragment("email=fragment%40example.com&token=first&token=second");

        var cut = RenderPage(Email, token: null);

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-success]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(Email, "first", It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void Fragment_WithOnlyOneValue_LandsOnTheFormWithoutPosting()
    {
        UseFragment("email=grace%40example.com");

        var cut = RenderPage(email: null, token: null);

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-submit]"));
        cut.Find("input[data-testid=confirm-email-address]").GetAttribute("value").Should().Be("grace@example.com");
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFragment_FallsBackToTheQueryValues(string fragment)
    {
        UseFragment(fragment);

        var cut = RenderPage(Email, "tok-123");

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-success]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(Email, "tok-123", It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // Store Run 5 D3: a new link opened in a tab already on /confirm-email (after "link expired, send me
    // a new one") only changes the fragment, so the page is not rebuilt. It must be read, replace the
    // stale values, be redeemed once and be scrubbed, exactly like a fresh tab.
    [Fact]
    public async Task NewLinkOnTheOpenPage_ReplacesTheStaleValues_AndIsRedeemedOnce()
    {
        Confirmation
            .Setup(x => x.ConfirmEmailAsync("old@example.com", "expired-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Unauthorized("Authentication.InvalidConfirmationToken", "Invalid token.", "Test")));
        var take = UseFragmentHandler("email=old%40example.com&token=expired-1");
        var cut = RenderPage(email: null, token: null);
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=confirm-email-error]"));

        take.SetResult("email=grace%40example.com&token=new-1");
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/confirm-email#email=grace%40example.com&token=new-1"));

        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=confirm-email-success]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync("grace@example.com", "new-1", It.IsAny<CancellationToken>()),
            Times.Once());
        JSInterop.Invocations.Count(i => i.Identifier == "locationHashTake").Should().Be(2, "taking the new fragment is what scrubs it");
    }

    [Fact]
    public async Task NewLinkOnTheOpenPage_CarryingOnlyTheAddress_ClearsTheOldTokenAndPostsNothing()
    {
        Confirmation
            .Setup(x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Unauthorized("Authentication.InvalidConfirmationToken", "Invalid token.", "Test")));
        var take = UseFragmentHandler("email=old%40example.com&token=expired-1");
        var cut = RenderPage(email: null, token: null);
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=confirm-email-error]"));

        take.SetResult("email=grace%40example.com");
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/confirm-email#email=grace%40example.com"));

        await cut.WaitForAssertionAsync(() =>
            cut.Find("input[data-testid=confirm-email-address]").GetAttribute("value").Should().Be("grace@example.com"));
        cut.Find("input[data-testid=confirm-email-token]").GetAttribute("value").Should().BeNullOrEmpty("the old link's token must not linger");
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task NavigationWithoutAFragment_TakesNothing()
    {
        UseFragment("email=grace%40example.com&token=t-1");
        var cut = RenderPage(email: null, token: null);
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=confirm-email-success]"));

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/confirm-email"));

        JSInterop.Invocations.Count(i => i.Identifier == "locationHashTake").Should().Be(1);
    }

    private JSRuntimeInvocationHandler<string?> UseFragmentHandler(string fragment)
    {
        Services.AddScoped<CapabilitiesJsModule>();
        var handler = JSInterop.SetupModule(CapabilitiesModulePath).Setup<string?>("locationHashTake");
        handler.SetResult(fragment);
        return handler;
    }

    private void UseFragment(string fragment)
    {
        Services.AddScoped<CapabilitiesJsModule>();
        JSInterop.SetupModule(CapabilitiesModulePath).Setup<string?>("locationHashTake").SetResult(fragment);
    }
}
