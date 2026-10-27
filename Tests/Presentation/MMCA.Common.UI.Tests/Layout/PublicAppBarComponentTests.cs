using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Layout;

/// <summary>
/// <see cref="IUIModule.AppBarComponentTypes"/> render for a signed-in user only, so a storefront
/// had no app bar slot for a control a signed-out shopper needs (site search, the cart).
/// <see cref="IUIModule.PublicAppBarComponentTypes"/> renders for every visitor, in the desktop app
/// bar and in the mobile quick-actions row, while the signed-in slot keeps its gate.
/// </summary>
public sealed class PublicAppBarComponentTests : BunitTestBase
{
    public PublicAppBarComponentTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<IOptions<LayoutSettings>>(
            Options.Create(new LayoutSettings { BrandName = "TestBrand" }));
        Services.AddSingleton<IUIModule>(new AppBarModule());
    }

    [Fact]
    public void IUIModule_DeclaresPublicAppBarComponentTypes_DefaultingToEmpty()
    {
        IUIModule bare = new BareModule();

        bare.PublicAppBarComponentTypes.Should().BeEmpty(
            "a module that contributes no public app bar control must not have to implement the member");
    }

    [Fact]
    public void MainLayout_WhenAnonymous_RendersThePublicComponentInTheAppBar_NotTheSignedInOne()
    {
        var cut = RenderLayout(signedIn: false);

        var appBar = cut.Find(".appbar-container");
        appBar.QuerySelectorAll("[data-testid=probe-public]").Should().ContainSingle(
            "a public app bar control is for every visitor");
        appBar.QuerySelector("[data-testid=probe-signed-in]").Should().BeNull(
            "AppBarComponentTypes stay gated on authentication");
    }

    [Fact]
    public void MainLayout_WhenSignedIn_RendersBothComponents()
    {
        var cut = RenderLayout(signedIn: true);

        var appBar = cut.Find(".appbar-container");
        appBar.QuerySelectorAll("[data-testid=probe-public]").Should().ContainSingle(
            "the public control is not dropped or repeated for a signed-in user");
        appBar.QuerySelectorAll("[data-testid=probe-signed-in]").Should().ContainSingle();
    }

    [Fact]
    public void NavMenu_WhenAnonymous_RendersThePublicComponentInTheQuickActionsRow()
    {
        RenderMudProviders(); // CultureSwitcher's MudMenu (mobile top-row) needs the popover provider.
        var cut = RenderUnderTest<NavMenu>(_ => { });

        var row = cut.Find(".toprow-actions");
        row.QuerySelectorAll("[data-testid=probe-public]").Should().ContainSingle(
            "phones reach the public control from the quick-actions row, where the app bar is hidden");
        row.QuerySelector("[data-testid=probe-signed-in]").Should().BeNull();
    }

    private IRenderedComponent<MainLayout> RenderLayout(bool signedIn)
    {
        // Same order as MainLayoutFooterTests: RendererInfo after every Services.Add call.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return signedIn
            ? RenderAs<MainLayout>(TestPrincipal.AuthenticatedUser(), WithBody)
            : RenderUnderTest<MainLayout>(WithBody);
    }

    private static void WithBody(ComponentParameterCollectionBuilder<MainLayout> parameters) =>
        parameters.Add(layout => layout.Body, builder => builder.AddContent(0, "Page body"));

    /// <summary>A module that contributes nothing beyond the required members.</summary>
    private sealed class BareModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(BareModule).Assembly;
    }

    /// <summary>A module contributing one public and one signed-in app bar component.</summary>
    private sealed class AppBarModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(AppBarModule).Assembly;

        public IReadOnlyList<Type> AppBarComponentTypes { get; } = [typeof(ProbeSignedIn)];

        public IReadOnlyList<Type> PublicAppBarComponentTypes { get; } = [typeof(ProbePublic)];
    }

    private sealed class ProbePublic : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "data-testid", "probe-public");
            builder.CloseElement();
        }
    }

    private sealed class ProbeSignedIn : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "data-testid", "probe-signed-in");
            builder.CloseElement();
        }
    }
}
