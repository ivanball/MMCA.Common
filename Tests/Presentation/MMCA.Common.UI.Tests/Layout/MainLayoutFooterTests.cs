using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// bUnit tests for the <see cref="MainLayout"/> footer's legal links: a link per configured document
/// and none for an unconfigured one, a footer that appears for the links alone (no footer text), and
/// no footer at all when neither is configured.
/// </summary>
public sealed class MainLayoutFooterTests : BunitTestBase
{
    private const string TermsUrl = "https://example.com/terms";
    private const string PrivacyUrl = "https://example.com/privacy";
    private const string ConductUrl = "https://example.com/conduct";

    public MainLayoutFooterTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
    }

    [Fact]
    public void WithSomeDocumentsConfigured_LinksOnlyThoseDocuments()
    {
        Configure(new LayoutSettings(), new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });

        var cut = RenderLayout();

        var links = cut.FindAll("footer .app-footer-links a");
        links.Select(a => a.GetAttribute("href")).Should().BeEquivalentTo(TermsUrl, PrivacyUrl);
        cut.Find("footer").TextContent.Should().Contain("Terms of Service").And.Contain("Privacy Policy")
            .And.NotContain("Code of Conduct");
    }

    [Fact]
    public void WithEveryDocumentConfigured_LinksAllThree()
    {
        Configure(
            new LayoutSettings(),
            new LegalSettings { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl, CodeOfConductUrl = ConductUrl });

        var cut = RenderLayout();

        cut.FindAll("footer .app-footer-links a").Select(a => a.GetAttribute("href"))
            .Should().BeEquivalentTo(TermsUrl, PrivacyUrl, ConductUrl);
    }

    [Fact]
    public void WithOnlyLegalLinksAndNoFooterText_StillRendersTheFooter()
    {
        Configure(new LayoutSettings { FooterText = string.Empty }, new LegalSettings { CodeOfConductUrl = ConductUrl });

        var cut = RenderLayout();

        cut.FindAll("footer.app-footer").Should().ContainSingle("the legal links alone are reason enough for a footer");
        cut.FindAll("footer .app-footer-links a").Select(a => a.GetAttribute("href"))
            .Should().ContainSingle().Which.Should().Be(ConductUrl);
    }

    [Fact]
    public void WithFooterTextOnly_RendersTheTextAndNoLegalLinks()
    {
        Configure(new LayoutSettings { FooterText = "(c) Example Inc." }, new LegalSettings());

        var cut = RenderLayout();

        cut.Find("footer.app-footer").TextContent.Should().Contain("(c) Example Inc.");
        cut.FindAll(".app-footer-links").Should().BeEmpty();
    }

    [Fact]
    public void WithNothingConfigured_RendersNoFooter()
    {
        Configure(new LayoutSettings { FooterText = string.Empty }, new LegalSettings());

        var cut = RenderLayout();

        cut.FindAll("footer").Should().BeEmpty();
        cut.FindAll(".app-footer-links").Should().BeEmpty();
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // MmcaThemeProviders reads RendererInfo after its first render. Set here, after every
        // Services.Add call (the constructor's and Configure's), because it freezes the provider.
        // No RenderMudProviders here: the layout renders MmcaThemeProviders, which already hosts them.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MainLayout>(_ => { });
    }

    private void Configure(LayoutSettings layout, LegalSettings legal)
    {
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(layout));
        Services.AddSingleton<IOptions<LegalSettings>>(Options.Create(legal));
    }
}
