using System.Text.RegularExpressions;
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
/// Keyboard bypass for the layout (WCAG 2.4.1 / 2.4.7, X-03). The skip link must move the user to the
/// main region of the page they are ON: a bare <c>href="#main-content"</c> resolves against the
/// document's <c>&lt;base href="/"&gt;</c> to <c>/#main-content</c>, which Blazor's router treats as a
/// navigation to the home page from anywhere but the root. The target must be focusable by script
/// (<c>tabindex="-1"</c>) so focus actually lands there, and the sidebar nav links need a visible
/// keyboard-focus indicator on the dark sidebar.
/// </summary>
public sealed partial class MainLayoutSkipLinkTests : BunitTestBase
{
    private const string MainContentFragment = "#main-content";

    public MainLayoutSkipLinkTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(new LayoutSettings()));
        Services.AddSingleton<IOptions<LegalSettings>>(Options.Create(new LegalSettings()));
    }

    [Fact]
    public void OnANonRootPage_TheSkipLinkStaysOnThatPage()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/conference/sessions?page=2");
        var currentPage = new Uri(navigation.Uri).GetLeftPart(UriPartial.Path);

        var cut = RenderLayout();

        var skipLink = cut.Find("a.skip-nav");
        var href = skipLink.GetAttribute("href");
        if (href is not null && !HasPreventDefaultHandler(skipLink))
        {
            // A real navigation happens: it must resolve to THIS page's main region, never to the root.
            var resolved = new Uri(new Uri(navigation.BaseUri), href);
            resolved.GetLeftPart(UriPartial.Path).Should().Be(
                currentPage,
                "under <base href=\"/\"> a bare \"#main-content\" resolves to the app root and Blazor navigates home");
            resolved.Fragment.Should().Be(MainContentFragment);
        }
        else
        {
            // No navigation at all: activation must be handled in-page (focus the main region).
            skipLink.Attributes.Select(a => a.Name).Should().Contain(
                n => n.StartsWith("blazor:onclick", StringComparison.OrdinalIgnoreCase),
                "a skip link that does not navigate must move focus itself");
        }
    }

    [Fact]
    public void TheMainRegion_IsProgrammaticallyFocusable()
    {
        var cut = RenderLayout();

        var main = cut.Find(MainContentFragment);

        main.GetAttribute("tabindex").Should().Be(
            "-1",
            "the skip target must accept focus, or focus stays on the skip link and the next Tab walks the sidebar again");
    }

    [Fact]
    public void NavMenuStylesheet_GivesNavLinksAVisibleKeyboardFocusIndicator()
    {
        var css = ReadEmbeddedCss("NavMenu.razor.css");

        var focusVisibleRule = NavLinkFocusVisibleRule.Match(css);

        focusVisibleRule.Success.Should().BeTrue(
            "the sidebar nav links need a :focus-visible rule; MudBlazor's default ring is invisible on the dark sidebar (WCAG 2.4.7)");
        focusVisibleRule.Groups["body"].Value.Should().MatchRegex(
            "(?i)outline|box-shadow",
            "the rule must draw a visible indicator");
    }

    // A rule whose selector targets .mud-nav-link in its :focus-visible state; captures the declarations.
    [GeneratedRegex(@"\.mud-nav-link[^{,]*:focus-visible[^{]*\{(?<body>[^}]*)\}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NavLinkFocusVisibleRule { get; }

    private static bool HasPreventDefaultHandler(AngleSharp.Dom.IElement element) =>
        element.Attributes.Any(a => a.Name.Contains("preventdefault", StringComparison.OrdinalIgnoreCase));

    private static string ReadEmbeddedCss(string logicalName)
    {
        using var stream = typeof(MainLayoutSkipLinkTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // Same order as MainLayoutFooterTests: RendererInfo after every Services.Add call.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MainLayout>(_ => { });
    }
}
