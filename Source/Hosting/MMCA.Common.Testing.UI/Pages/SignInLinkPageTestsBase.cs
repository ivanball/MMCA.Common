using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth.SignInLinks;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;
using Moq;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for the framework's anonymous <c>/auth/sign-in-link</c> page (<see cref="SignInLink"/>,
/// ADR-116): a complete link waits for Continue and is then redeemed exactly once, a redeemed link
/// lands on the configured path, a refusal offers a fresh link, an incomplete link never posts, and a
/// resend reports the same thing whether or not the address holds an account. The framework runs these
/// over its own page; a consumer that registers extra services for its pages keeps a one-line sealed
/// subclass so its host's registrations are exercised too (the ADR-015 pattern).
/// </summary>
/// <remarks>
/// The link arrives in the URI fragment, which only script can read, so the facts answer the page's
/// <c>locationHashTake</c> call through bUnit's JS interop with the fragment a real link carries.
/// </remarks>
public abstract class SignInLinkPageTestsBase : BunitComponentTestBase
{
    private const string ModulePath = "./_content/MMCA.Common.UI/capabilities-interop.js";

    protected SignInLinkPageTestsBase()
    {
        Services.AddSingleton(SignInLinks.Object);
        Services.AddScoped<CapabilitiesJsModule>();
        Services.AddSingleton(Options.Create(new SignInSettings { SignInLinkLandingPath = LandingPath }));

        // Every Result-returning member needs an explicit setup: an unstubbed Task<Result> hands back
        // null rather than a success, which the page would dereference.
        SignInLinks
            .Setup(x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AuthenticationResponse("access", "refresh", DateTime.UtcNow.AddMinutes(15))));
        SignInLinks
            .Setup(x => x.RequestSignInLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    /// <summary>Gets the mocked sign-in-link client the page calls.</summary>
    protected Mock<ISignInLinkUIService> SignInLinks { get; } = new();

    /// <summary>Gets the address the facts put in the link. Override to use an app-shaped one.</summary>
    protected virtual string Email => "ada@example.com";

    /// <summary>Gets the landing path the facts configure. Override to match the app's.</summary>
    protected virtual string LandingPath => "/my-schedule";

    [Fact]
    public void WithACompleteLink_ShowsTheAddressAndWaitsForContinue()
    {
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}&token=tok-123");

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid=sign-in-link-address]").TextContent.Should().Contain(Email));

        // A mail scanner that renders the page must not spend the token: nothing posts before the tap.
        SignInLinks.Verify(
            x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void Continue_RedeemsOnceAndLandsOnTheConfiguredPath()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}&token=tok-123");
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-continue]"));

        cut.Find("[data-testid=sign-in-link-continue]").Click();

        cut.WaitForAssertion(() => navigation.Uri.Should().EndWith(LandingPath));
        SignInLinks.Verify(
            x => x.RedeemSignInLinkAsync(Email, "tok-123", It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void WithARefusedLink_ShowsTheRefusalAndOffersAFreshLink()
    {
        SignInLinks
            .Setup(x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Authentication.InvalidSignInLink", "Invalid link.", "Test")));
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}&token=stale");
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-continue]"));

        cut.Find("[data-testid=sign-in-link-continue]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=sign-in-link-error]");
            cut.Find("[data-testid=sign-in-link-request]");
            cut.Find("[data-testid=sign-in-link-email]");
        });
    }

    [Fact]
    public void WithAnIncompleteLink_DoesNotPostAndOffersAFreshLink()
    {
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}");

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=sign-in-link-error]");
            cut.Find("[data-testid=sign-in-link-request]");
        });
        SignInLinks.Verify(
            x => x.RedeemSignInLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The endpoint answers 202 either way, so the notice says "if that address has an account" rather
    /// than promising a delivered email.
    /// </summary>
    [Fact]
    public void RequestingAFreshLink_SaysSoWithoutConfirmingTheAccountExists()
    {
        var cut = RenderPage($"email={Uri.EscapeDataString(Email)}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=sign-in-link-request]"));

        cut.Find("[data-testid=sign-in-link-request]").Click();

        cut.WaitForAssertion(() =>
        {
            SignInLinks.Verify(x => x.RequestSignInLinkAsync(Email, It.IsAny<CancellationToken>()), Times.Once());
            cut.Find("[data-testid=sign-in-link-sent]");
        });
    }

    /// <summary>Renders the page with <paramref name="fragment"/> as the link's URI fragment.</summary>
    /// <param name="fragment">The fragment without its leading hash.</param>
    /// <returns>The rendered page.</returns>
    protected IRenderedComponent<SignInLink> RenderPage(string fragment)
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("locationHashTake", _ => true).SetResult(fragment);

        RenderMudProviders();
        return RenderUnderTest<SignInLink>(_ => { });
    }
}
