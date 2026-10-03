using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Legal;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.Services.Legal;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Legal;

/// <summary>
/// bUnit tests for <see cref="TermsAcceptanceGate"/>: it asks nothing of an anonymous visitor, opens
/// its dialog only for a signed-in user whose standing names a current version they have not
/// accepted (headed "first" or "updated" by whether they ever accepted one), keeps Accept disabled
/// until the box is ticked, records the version it showed, and stays out of the way on any failed
/// or current read. Sign out goes through the existing logout path.
/// </summary>
public sealed class TermsAcceptanceGateTests : BunitTestBase
{
    private const string AgreeSelector = "input[data-testid='terms-acceptance-gate-agree']";
    private const string AcceptSelector = "button[data-testid='terms-acceptance-gate-accept']";
    private const string FirstHeading = "Please review our terms";
    private const string UpdatedHeading = "We've updated our terms";

    private readonly Mock<ILegalAcceptanceUIService> _legal = new();
    private readonly Mock<IAuthUIService> _auth = new();

    public TermsAcceptanceGateTests()
    {
        Services.AddSingleton(_legal.Object);
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        _auth.Setup(a => a.LogoutAsync()).Returns(Task.CompletedTask);
    }

    // ── Inert states ──
    [Fact]
    public void WhenAnonymous_ShowsNoDialogAndNeverReadsTheStanding()
    {
        var providers = RenderMudProviders();

        RenderUnderTest<TermsAcceptanceGate>(_ => { });

        providers.Dialog.FindAll("h2").Should().BeEmpty();
        _legal.Verify(l => l.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void WhenTheReadFails_ShowsNoDialog()
    {
        StandingIs(Result.Failure<LegalAcceptanceDTO>(Error.NotFoundError("Http.404", "Not found.")));

        var providers = RenderSignedIn();

        AssertReadThenNoDialog(providers);
    }

    [Fact]
    public void WhenNoVersionIsConfigured_ShowsNoDialog()
    {
        StandingIs(Result.Success(new LegalAcceptanceDTO { CurrentVersion = null, IsCurrent = false }));

        var providers = RenderSignedIn();

        AssertReadThenNoDialog(providers);
    }

    [Fact]
    public void WhenTheUserIsCurrent_ShowsNoDialog()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v2", DateTime.UtcNow)));

        var providers = RenderSignedIn();

        AssertReadThenNoDialog(providers);
    }

    // ── The dialog ──
    [Fact]
    public void WhenTheUserNeverAccepted_ShowsTheFirstTimeHeading()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", acceptedVersion: null, acceptedOn: null)));

        var providers = RenderSignedIn();

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.Find("h2").TextContent.Trim().Should().Be(FirstHeading));
    }

    [Fact]
    public void WhenTheUserAcceptedAnOlderVersion_ShowsTheUpdatedHeading()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v1", DateTime.UtcNow)));

        var providers = RenderSignedIn();

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.Find("h2").TextContent.Trim().Should().Be(UpdatedHeading));
    }

    [Fact]
    public void Accept_IsDisabledUntilTheBoxIsTicked()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v1", DateTime.UtcNow)));
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() => providers.Dialog.FindAll(AcceptSelector).Should().ContainSingle());

        providers.Dialog.Find(AcceptSelector).HasAttribute("disabled").Should().BeTrue();

        providers.Dialog.Find(AgreeSelector).Change(true);

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.Find(AcceptSelector).HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void AcceptingRecordsTheShownVersionAndClosesTheDialog()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v1", DateTime.UtcNow)));
        _legal
            .Setup(l => l.AcceptAsync("v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v2", DateTime.UtcNow)));
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() => providers.Dialog.FindAll(AgreeSelector).Should().ContainSingle());

        providers.Dialog.Find(AgreeSelector).Change(true);
        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.Find(AcceptSelector).HasAttribute("disabled").Should().BeFalse());
        providers.Dialog.Find(AcceptSelector).Click();

        providers.Dialog.WaitForAssertion(() => providers.Dialog.FindAll("h2").Should().BeEmpty());
        _legal.Verify(l => l.AcceptAsync("v2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SigningOut_UsesTheExistingLogoutPath()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", acceptedVersion: null, acceptedOn: null)));
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() => providers.Dialog.FindAll("h2").Should().ContainSingle());

        providers.Dialog.FindButtonByText("Sign out").Click();

        providers.Dialog.WaitForAssertion(() => _auth.Verify(a => a.LogoutAsync(), Times.Once));
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/login");
        _legal.Verify(l => l.AcceptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void StandingIs(Result<LegalAcceptanceDTO> standing) =>
        _legal.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(standing);

    private MudProviderHandles RenderSignedIn()
    {
        var providers = RenderMudProviders();
        RenderAs<TermsAcceptanceGate>(TestPrincipal.AuthenticatedUser(), _ => { });
        return providers;
    }

    private void AssertReadThenNoDialog(MudProviderHandles providers)
    {
        providers.Dialog.WaitForAssertion(() =>
            _legal.Verify(l => l.GetAsync(It.IsAny<CancellationToken>()), Times.Once));
        providers.Dialog.FindAll("h2").Should().BeEmpty();
        providers.Dialog.FindAll(AcceptSelector).Should().BeEmpty();
    }
}
