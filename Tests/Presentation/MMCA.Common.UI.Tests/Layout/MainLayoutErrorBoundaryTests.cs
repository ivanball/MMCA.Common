using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// A12: <see cref="MainLayout"/> wraps the page in an error boundary, so a page that throws while
/// rendering shows the localized error state (an alert plus Retry) inside the shell instead of ending
/// the whole circuit, and navigating to another page recovers the boundary.
/// </summary>
public sealed class MainLayoutErrorBoundaryTests : BunitTestBase
{
    private const string PageText = "The page rendered.";

    private readonly ThrowSwitch _throw = new() { Throw = true };

    public MainLayoutErrorBoundaryTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton(_throw);
    }

    [Fact]
    public void APageThatThrows_RendersTheErrorStateInsideTheShell()
    {
        var cut = RenderLayout();

        var error = cut.Find("[data-testid=page-error]");
        error.QuerySelector("[role=alert]").Should().NotBeNull("the error announces itself like the other page states");
        error.TextContent.Should().Contain("could not be displayed").And.Contain("Retry");
        cut.FindAll("article#main-content").Should().ContainSingle("the shell around the page keeps rendering");
        cut.Markup.Should().NotContain(PageText);
    }

    [Fact]
    public async Task NavigatingAway_RecoversTheBoundary()
    {
        var cut = RenderLayout();
        cut.FindAll("[data-testid=page-error]").Should().ContainSingle("precondition: the page failed");

        _throw.Throw = false;
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/elsewhere"));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("[data-testid=page-error]").Should().BeEmpty("a new location gets a fresh attempt");
            cut.Markup.Should().Contain(PageText);
        });
    }

    [Fact]
    public async Task Retry_RecoversTheBoundary()
    {
        var cut = RenderLayout();

        _throw.Throw = false;
        await cut.Find("[data-testid=page-error] button").ClickAsync(new());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(PageText));
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // Set after every Services.Add call, because it freezes the provider (see MainLayoutFooterTests).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MainLayout>(p => p.Add(c => c.Body, builder =>
        {
            builder.OpenComponent<ThrowingPage>(0);
            builder.CloseComponent();
        }));
    }

    /// <summary>Whether <see cref="ThrowingPage"/> throws on its next render.</summary>
    private sealed class ThrowSwitch
    {
        public bool Throw { get; set; }
    }

    /// <summary>A page that throws while rendering until its switch is turned off.</summary>
    private sealed class ThrowingPage : ComponentBase
    {
        [Inject] private ThrowSwitch Switch { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Switch.Throw)
            {
                throw new InvalidOperationException("The page failed to render.");
            }

            builder.AddContent(0, PageText);
        }
    }
}
