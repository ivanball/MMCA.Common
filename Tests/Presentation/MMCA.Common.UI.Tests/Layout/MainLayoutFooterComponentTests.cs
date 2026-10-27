using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// <see cref="IUIModule.FooterComponentTypes"/>: a module's footer content renders inside the layout's
/// one <c>contentinfo</c> footer (after the footer text and legal links), the footer appears for module
/// content alone, and a module contributing none leaves the footer exactly as before. Visible content
/// registered as a layout component instead becomes an extra column in the desktop flex row, which is
/// what the slot exists to avoid.
/// </summary>
public sealed class MainLayoutFooterComponentTests : BunitTestBase
{
    public MainLayoutFooterComponentTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
    }

    [Fact]
    public void IUIModule_FooterComponentTypes_DefaultsToEmpty()
    {
        IUIModule module = new BareModule();

        module.FooterComponentTypes.Should().BeEmpty(
            "a module that contributes no footer content must not have to implement the member");
    }

    [Fact]
    public void FooterComponent_RendersInsideTheFooter_AfterTheFooterText()
    {
        Services.AddSingleton<IUIModule>(new FooterModule());
        var cut = RenderLayout(new LayoutSettings { FooterText = "(c) Example Inc." });

        var footer = cut.Find("footer.app-footer[role=contentinfo]");
        footer.QuerySelector("[data-testid=probe-footer]").Should().NotBeNull(
            "module footer content belongs inside the contentinfo landmark");
        footer.TextContent.IndexOf("(c) Example Inc.", StringComparison.Ordinal).Should().BeLessThan(
            footer.TextContent.IndexOf("Probe footer", StringComparison.Ordinal),
            "the module's content follows the configured footer text");
    }

    [Fact]
    public void FooterComponentAlone_IsReasonEnoughForAFooter()
    {
        Services.AddSingleton<IUIModule>(new FooterModule());
        var cut = RenderLayout(new LayoutSettings { FooterText = string.Empty });

        cut.FindAll("footer.app-footer").Should().ContainSingle(
            "a footer renders for module content even with no footer text and no legal links");
        cut.FindAll("[data-testid=probe-footer]").Should().ContainSingle("the content renders exactly once");
    }

    [Fact]
    public void ModuleWithoutFooterContent_LeavesTheFooterAsItWas()
    {
        Services.AddSingleton<IUIModule>(new BareModule());
        var cut = RenderLayout(new LayoutSettings { FooterText = string.Empty });

        cut.FindAll("footer").Should().BeEmpty("nothing configured and nothing contributed means no footer, as before");
    }

    private IRenderedComponent<MainLayout> RenderLayout(LayoutSettings layout)
    {
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(layout));
        Services.AddSingleton<IOptions<LegalSettings>>(Options.Create(new LegalSettings()));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MainLayout>(_ => { });
    }

    private sealed class BareModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(BareModule).Assembly;
    }

    private sealed class FooterModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(FooterModule).Assembly;

        public IReadOnlyList<Type> FooterComponentTypes { get; } = [typeof(ProbeFooter)];
    }

    private sealed class ProbeFooter : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "nav");
            builder.AddAttribute(1, "data-testid", "probe-footer");
            builder.AddContent(2, "Probe footer");
            builder.CloseElement();
        }
    }
}
