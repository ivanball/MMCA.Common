using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.Shared.Legal;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Legal;
using MMCA.Common.UI.Services.Api;
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
    private const string AcceptFailedEnglish = "We could not record your acceptance. Please try again.";

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

    // ── Navigation must not dismiss the gate (A-45) ──
    // MudBlazor's dialog provider closes every open dialog on LocationChanged, and a list page
    // rewrites its URL (page/sort/filter query) right after it loads. The gate must not let that
    // close the consent dialog for the rest of the session while the terms are still unaccepted.
    [Fact]
    public void ANavigationWhileTheTermsAreUnaccepted_LeavesTheDialogShowing()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v1", DateTime.UtcNow)));
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.FindAll("h2").Should().ContainSingle());

        Services.GetRequiredService<NavigationManager>().NavigateTo("/events?page=2&sort=name", replace: true);

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.FindAll("h2").Should().ContainSingle(
                "the terms are still unaccepted, so a URL change must not dismiss the consent dialog"));
        providers.Dialog.FindAll(AcceptSelector).Should().ContainSingle();
    }

    [Fact]
    public void ANavigationToAnotherPage_LeavesTheDialogShowing()
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", acceptedVersion: null, acceptedOn: null)));
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.FindAll("h2").Should().ContainSingle());

        Services.GetRequiredService<NavigationManager>().NavigateTo("/sessions");

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.FindAll("h2").Should().ContainSingle(
                "consent is still owed after navigating, so the gate must still be asking for it"));
    }

    // ── A failed accept (A-45, ADC local test run 6) ──
    // Every HTTP failure carries a synthesized English message ("The request failed with HTTP status
    // code 500.", the transport and timeout wording), so falling back to Legal.Gate.AcceptFailed only
    // when the result had NO message meant the localized sentence was never shown. A server or
    // transport fault gets the gate's own wording; only a validation refusal, which the server
    // phrased for the user, is shown verbatim.
    public static TheoryData<string> NonValidationFailures =>
    [
        "transport",
        "timeout",
        "500-bodiless",
        "503-bodiless",
        "500-problem-body",
    ];

    [Theory]
    [MemberData(nameof(NonValidationFailures))]
    public async Task AFailedAccept_ThatIsNotAValidationRefusal_ShowsTheLocalizedAcceptFailedMessage(string failure)
    {
        var acceptResult = await NonValidationFailure(failure);
        var alert = AcceptAndReadTheAlert(acceptResult);

        alert.Should().Be(
            AcceptFailedEnglish,
            "a server or transport fault is not something the user can act on beyond retrying, so the gate shows its own localized wording");
        alert.Should().NotContain("The request failed with HTTP status code");
    }

    [Fact]
    public void AValidationRefusalFromTheServer_StillShowsTheServersMessage()
    {
        const string serverMessage = "The terms version you accepted is not valid.";
        const string body =
            """{"title":"Validation failed","status":400,"errors":[{"code":"Legal.Acceptance.Invalid","message":"The terms version you accepted is not valid.","type":"Validation"}]}""";
        var acceptResult = Result.Failure<LegalAcceptanceDTO>(ProblemDetailsResultReader.ParseProblemDetails(400, body));

        var alert = AcceptAndReadTheAlert(acceptResult);

        alert.Should().Be(serverMessage, "a validation refusal is phrased for the user by the server");
    }

    private static async Task<Result<LegalAcceptanceDTO>> NonValidationFailure(string failure) => failure switch
    {
        "transport" => await HttpResultExecutor.ExecuteAsync(
            () => Task.FromException<Result<LegalAcceptanceDTO>>(new HttpRequestException("Connection refused")),
            CancellationToken.None),
        "timeout" => await HttpResultExecutor.ExecuteAsync(
            () => Task.FromException<Result<LegalAcceptanceDTO>>(new TaskCanceledException("HttpClient.Timeout")),
            CancellationToken.None),
        "500-bodiless" => Result.Failure<LegalAcceptanceDTO>(ProblemDetailsResultReader.ParseProblemDetails(500, null)),
        "503-bodiless" => Result.Failure<LegalAcceptanceDTO>(ProblemDetailsResultReader.ParseProblemDetails(503, string.Empty)),
        "500-problem-body" => Result.Failure<LegalAcceptanceDTO>(ProblemDetailsResultReader.ParseProblemDetails(
            500,
            """{"title":"An error occurred while processing your request.","status":500,"detail":"An unexpected error occurred."}""")),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "unknown failure shape"),
    };

    private string AcceptAndReadTheAlert(Result<LegalAcceptanceDTO> acceptResult)
    {
        StandingIs(Result.Success(LegalAcceptanceDTO.Evaluate("v2", "v1", DateTime.UtcNow)));
        _legal
            .Setup(l => l.AcceptAsync("v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(acceptResult);
        var providers = RenderSignedIn();
        providers.Dialog.WaitForAssertion(() => providers.Dialog.FindAll(AgreeSelector).Should().ContainSingle());

        providers.Dialog.Find(AgreeSelector).Change(true);
        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.Find(AcceptSelector).HasAttribute("disabled").Should().BeFalse());
        providers.Dialog.Find(AcceptSelector).Click();

        providers.Dialog.WaitForAssertion(() =>
            providers.Dialog.FindAll("[role='alert']").Should().ContainSingle("a failed accept keeps the dialog open with one alert"));
        return providers.Dialog.Find("[role='alert']").TextContent.Trim();
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
