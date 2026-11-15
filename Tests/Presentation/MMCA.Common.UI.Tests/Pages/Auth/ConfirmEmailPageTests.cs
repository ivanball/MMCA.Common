using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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

    private void UseFragment(string fragment)
    {
        Services.AddScoped<CapabilitiesJsModule>();
        JSInterop.SetupModule(CapabilitiesModulePath).Setup<string?>("locationHashTake").SetResult(fragment);
    }
}
