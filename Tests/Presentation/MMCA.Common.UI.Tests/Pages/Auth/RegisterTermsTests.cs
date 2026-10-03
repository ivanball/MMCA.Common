using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// bUnit tests for the Terms of Service checkbox on the Register page: absent (and the submit
/// unchanged) until the host publishes a Terms URL; once it does, the submit stays disabled until
/// the box is ticked and the request carries the acceptance; the Code of Conduct is named only when
/// the host publishes one.
/// </summary>
public sealed class RegisterTermsTests : BunitTestBase
{
    private const string TermsSelector = "[data-testid='register-accept-terms']";
    private const string TermsUrl = "https://example.com/terms";
    private const string PrivacyUrl = "https://example.com/privacy";
    private const string ConductUrl = "https://example.com/conduct";

    private readonly Mock<IAuthUIService> _auth = new();

    public RegisterTermsTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AuthenticationResponse(
                "access-token",
                "refresh-token",
                new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc))));
    }

    // ── No Terms URL: the page is unchanged ──
    [Fact]
    public void WithoutATermsUrl_ShowsNoTermsCheckbox_AndSubmitsAsBefore()
    {
        var cut = RenderUnderTest<Register>(_ => { });

        cut.FindAll(TermsSelector).Should().BeEmpty();
        FillRequiredFields(cut);
        cut.Find("button[type='submit']").HasAttribute("disabled").Should().BeFalse();

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => _auth.Verify(
            x => x.RegisterAsync(It.Is<RegisterRequest>(r => !r.AcceptedTerms), It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    // ── Terms URL configured ──
    [Fact]
    public void WithATermsUrl_ShowsTheCheckboxAndKeepsSubmitDisabledUntilItIsTicked()
    {
        UseLegal(new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.FindAll(TermsSelector).Should().ContainSingle();
        cut.Find("button[type='submit']").HasAttribute("disabled").Should().BeTrue(
            "the registrant has not agreed yet");

        TickTerms(cut);

        cut.WaitForAssertion(() =>
            cut.Find("button[type='submit']").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void WithATermsUrl_SubmittingAfterTickingSendsAcceptedTerms()
    {
        UseLegal(new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);
        TickTerms(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => _auth.Verify(
            x => x.RegisterAsync(It.Is<RegisterRequest>(r => r.AcceptedTerms), It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Fact]
    public void WithATermsUrl_TheSentenceLinksTheDocuments()
    {
        UseLegal(new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });
        var cut = RenderUnderTest<Register>(_ => { });

        var hrefs = cut.Find(TermsSelector).QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();

        hrefs.Should().Contain(TermsUrl).And.Contain(PrivacyUrl);
    }

    // ── Code of Conduct phrase ──
    [Fact]
    public void WithoutACodeOfConductUrl_TheSentenceDoesNotMentionIt()
    {
        UseLegal(new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });
        var cut = RenderUnderTest<Register>(_ => { });

        var sentence = cut.Find(TermsSelector).TextContent;

        sentence.Should().NotContain("Code of Conduct");
        sentence.Should().NotContain("and to follow the");
    }

    [Fact]
    public void WithACodeOfConductUrl_TheSentenceNamesAndLinksIt()
    {
        UseLegal(new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl, CodeOfConductUrl = ConductUrl });
        var cut = RenderUnderTest<Register>(_ => { });

        var block = cut.Find(TermsSelector);

        block.TextContent.Should().Contain("and to follow the").And.Contain("Code of Conduct");
        block.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Contain(ConductUrl);
    }

    private static void TickTerms(IRenderedComponent<Register> cut) =>
        cut.Find(TermsSelector + " input[type='checkbox']").Change(true);

    private static void FillRequiredFields(IRenderedComponent<Register> cut)
    {
        cut.Find("input[autocomplete='given-name']").Input("Ada");
        cut.Find("input[autocomplete='family-name']").Input("Lovelace");
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.FindAll("input[autocomplete='new-password']")[0].Input("Str0ng!Passw0rd");
        cut.FindAll("input[autocomplete='new-password']")[1].Input("Str0ng!Passw0rd");
    }

    private void UseLegal(LegalSettings settings) =>
        Services.AddSingleton<IOptions<LegalSettings>>(Options.Create(settings));
}
