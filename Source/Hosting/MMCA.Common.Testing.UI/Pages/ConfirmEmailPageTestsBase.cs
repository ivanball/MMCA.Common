using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for the framework's anonymous <c>/confirm-email</c> page (<see cref="ConfirmEmail"/>,
/// ADR-116): a complete link is redeemed exactly once on arrival, an incomplete one lands on manual
/// entry without posting, a refusal keeps the form with the API's own message, and a resend reports
/// the same thing whether or not the address holds an account. The framework runs these over its own
/// page; a consumer that registers extra services for its pages keeps a one-line sealed subclass so
/// its host's registrations are exercised too (the ADR-015 pattern).
/// </summary>
/// <remarks>
/// The fragment path is not exercised: <c>locationHashTake</c> is a JS call and bUnit has no browser
/// to hold a fragment. The query-parameter path the page shares with it is what these drive, which is
/// also the fallback a mail client that rewrote the link produces.
/// </remarks>
public abstract class ConfirmEmailPageTestsBase : BunitComponentTestBase
{
    protected ConfirmEmailPageTestsBase()
    {
        Services.AddSingleton(Confirmation.Object);

        // Every Result-returning member needs an explicit setup: an unstubbed Task<Result> hands back
        // null rather than a success, which the page would dereference.
        Confirmation
            .Setup(x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        Confirmation
            .Setup(x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    /// <summary>Gets the mocked confirmation client the page calls.</summary>
    protected Mock<IEmailConfirmationUIService> Confirmation { get; } = new();

    /// <summary>Gets the address the facts put in the link. Override to use an app-shaped one.</summary>
    protected virtual string Email => "ada@example.com";

    [Fact]
    public void WithACompleteLink_ConfirmsOnceAndReportsSuccess()
    {
        var cut = RenderPage(Email, "tok-123");

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-success]"));

        // A further render (parameters set again, a re-render after the success) must not spend the
        // single-use token a second time.
        cut.Render();
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(Email, "tok-123", It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void WithARefusedToken_ShowsTheApisMessage_AndKeepsTheFormWithAResend()
    {
        Confirmation
            .Setup(x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Authentication.InvalidConfirmationToken", "Invalid token.", "Test")));

        var cut = RenderPage(Email, "stale");

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=confirm-email-error]").TextContent.Should().Contain("Invalid token.");
            cut.Find("[data-testid=confirm-email-resend]");
            cut.Find("[data-testid=confirm-email-submit]");
            cut.FindAll("[data-testid=confirm-email-success]").Should().BeEmpty();
        });
    }

    /// <summary>
    /// A link with nothing to redeem must not post a request that can only be refused; it lands
    /// straight on manual entry, with no error until the visitor tries.
    /// </summary>
    [Fact]
    public void WithNoToken_DoesNotCallTheServiceAndShowsTheForm()
    {
        var cut = RenderPage(Email, token: null);

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=confirm-email-address]");
            cut.Find("[data-testid=confirm-email-token]");
            cut.FindAll("[data-testid=confirm-email-error]").Should().BeEmpty();
        });
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void SubmittingWithoutAToken_ExplainsWhatIsMissingAndDoesNotCallTheService()
    {
        var cut = RenderPage(Email, token: null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-submit]"));

        cut.Find("[data-testid=confirm-email-submit]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-error]"));
        Confirmation.Verify(
            x => x.ConfirmEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The endpoint answers 202 either way, so the notice says "if that address has an unconfirmed
    /// account" rather than promising a delivered email.
    /// </summary>
    [Fact]
    public void Resend_AsksForAFreshLinkAndSaysSoWithoutConfirmingTheAccountExists()
    {
        var cut = RenderPage(Email, token: null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-resend]"));

        cut.Find("[data-testid=confirm-email-resend]").Click();

        cut.WaitForAssertion(() =>
        {
            Confirmation.Verify(x => x.ResendEmailConfirmationAsync(Email, It.IsAny<CancellationToken>()), Times.Once());
            cut.Find("[data-testid=confirm-email-resent]").TextContent.Should().Contain("unconfirmed account");
        });
    }

    [Fact]
    public void ResendWithNoAddress_DoesNotCallTheService()
    {
        var cut = RenderPage(email: null, token: null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-resend]"));

        cut.Find("[data-testid=confirm-email-resend]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-email-error]"));
        Confirmation.Verify(
            x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Renders the page the way the emailed link reaches it. <c>[SupplyParameterFromQuery]</c> values
    /// cannot be passed as component parameters: bUnit routes them through its
    /// <see cref="NavigationManager"/> exactly as the browser would, so this navigates to the link.
    /// </summary>
    /// <param name="email">The address in the link, or null to leave it out.</param>
    /// <param name="token">The token in the link, or null to leave it out.</param>
    /// <returns>The rendered page.</returns>
    protected IRenderedComponent<ConfirmEmail> RenderPage(string? email, string? token)
    {
        RenderMudProviders();

        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameters(
            "/confirm-email",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["email"] = email,
                ["token"] = token,
            }));

        return RenderUnderTest<ConfirmEmail>(_ => { });
    }
}
