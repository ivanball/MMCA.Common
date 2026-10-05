using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// C-16 support (Store run 1): a module had no layout slot that renders ABOVE the page body, so a
/// banner registered as a <see cref="IUIModule.LayoutComponentTypes"/> entry rendered after the
/// footer, at the bottom of the page. <see cref="IUIModule"/> gains
/// <c>IReadOnlyList&lt;Type&gt; ContentHeaderComponentTypes =&gt; []</c>, and
/// <see cref="MainLayout"/> renders each of those components inside <c>#main-content</c>, before the
/// page body (beside the offline banner).
/// <para>
/// The probe module declares <c>ContentHeaderComponentTypes</c> as an ordinary public property, so
/// this file compiles before the interface member exists; once it does, the property implements it.
/// </para>
/// </summary>
public sealed class MainLayoutContentHeaderTests : BunitTestBase
{
    private const string ContentHeaderMember = "ContentHeaderComponentTypes";

    public MainLayoutContentHeaderTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
    }

    [Fact]
    public void IUIModule_DeclaresContentHeaderComponentTypes_DefaultingToEmpty()
    {
        var member = typeof(IUIModule).GetProperty(ContentHeaderMember, BindingFlags.Public | BindingFlags.Instance);

        member.Should().NotBeNull($"IUIModule needs a {ContentHeaderMember} slot for components rendered above the page body");
        member!.PropertyType.Should().Be<IReadOnlyList<Type>>();
        ((IEnumerable<Type>)member.GetValue(new BareModule())!).Should().BeEmpty(
            "a module that contributes no content header must not have to implement the member");
    }

    [Fact]
    public void ContentHeaderComponent_RendersInsideMainContent_BeforeThePageBody()
    {
        Services.AddSingleton<IUIModule>(new HeaderModule());
        var cut = RenderLayout();

        var main = cut.Find("#main-content");
        var header = main.QuerySelector("[data-testid=probe-content-header]");
        header.Should().NotBeNull("a module's content-header component renders inside the main content region");

        var body = main.QuerySelector("[data-testid=probe-body]");
        body.Should().NotBeNull("the page body renders in the main content region (precondition)");

        var ordered = main.QuerySelectorAll("[data-testid]").Select(e => e.GetAttribute("data-testid")).ToList();
        ordered.IndexOf("probe-content-header").Should().BeLessThan(
            ordered.IndexOf("probe-body"),
            "the content header sits above the page, not below it");
    }

    [Fact]
    public void ContentHeaderComponent_RendersExactlyOnce()
    {
        Services.AddSingleton<IUIModule>(new HeaderModule());
        var cut = RenderLayout();

        cut.FindAll("[data-testid=probe-content-header]").Should().ContainSingle(
            "a content header is not also rendered in the layout-level slot");
    }

    [Fact]
    public void LayoutComponent_StillRendersOutsideMainContent()
    {
        Services.AddSingleton<IUIModule>(new HeaderModule());
        var cut = RenderLayout();

        cut.FindAll("[data-testid=probe-layout-component]").Should().ContainSingle();
        cut.Find("#main-content").QuerySelector("[data-testid=probe-layout-component]").Should().BeNull(
            "LayoutComponentTypes keep rendering at the root layout level (drawers, overlays)");
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // Same order as MainLayoutFooterTests: RendererInfo after every Services.Add call.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderUnderTest<MainLayout>(p => p.Add(
            layout => layout.Body,
            builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddAttribute(1, "data-testid", "probe-body");
                builder.AddContent(2, "Page body");
                builder.CloseElement();
            }));
    }

    /// <summary>A module that contributes nothing beyond the required members.</summary>
    private sealed class BareModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(BareModule).Assembly;
    }

    /// <summary>A module contributing one content-header component and one layout-level component.</summary>
    private sealed class HeaderModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(HeaderModule).Assembly;

        public IReadOnlyList<Type> LayoutComponentTypes { get; } = [typeof(ProbeLayoutComponent)];

        // Implements IUIModule.ContentHeaderComponentTypes once the interface declares it.
        public IReadOnlyList<Type> ContentHeaderComponentTypes { get; } = [typeof(ProbeContentHeader)];
    }

    private sealed class ProbeContentHeader : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", "probe-content-header");
            builder.AddContent(2, "Content header");
            builder.CloseElement();
        }
    }

    private sealed class ProbeLayoutComponent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", "probe-layout-component");
            builder.CloseElement();
        }
    }
}
