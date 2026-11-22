using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.TwoFactor;
using Moq;

namespace MMCA.Common.UI.Tests.TwoFactor;

/// <summary>
/// The opt-in, as rendered (design 2.2.4 and 3.1): only a host that called <c>AddTwoFactorUI()</c>
/// shows the two-factor nav item to a signed-in user and mounts the "second factor required" listener in
/// the layout. A host that adopts nothing renders neither, so nothing ever subscribes and no 403 body is
/// read on its behalf.
/// </summary>
public sealed class TwoFactorOptInRenderTests : BunitTestBase
{
    private const string AccountLink = "a[href='/profile/two-factor']";

    public TwoFactorOptInRenderTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<ITokenStorageService>(new StubTokenStorageService());
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(new LayoutSettings { BrandName = "TestBrand" }));
    }

    [Fact]
    public void WithAddTwoFactorUI_TheSignedInNavShowsTheAccountPage()
    {
        Services.AddTwoFactorUI();
        RenderMudProviders();

        var cut = RenderAs<NavMenu>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.FindAll(AccountLink).Should().ContainSingle()
            .Which.TextContent.Should().Contain("Two-factor authentication");
    }

    [Fact]
    public void WithoutAddTwoFactorUI_TheSignedInNavHasNoTwoFactorItem()
    {
        RenderMudProviders();

        var cut = RenderAs<NavMenu>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.FindAll(AccountLink).Should().BeEmpty("a host that adopts nothing shows no two-factor item");
    }

    [Fact]
    public void WithAddTwoFactorUI_TheLayoutMountsTheListenerOnce()
    {
        Services.AddTwoFactorUI();

        var cut = RenderLayout();

        cut.FindComponents<MultiFactorRequiredListener>().Should().ContainSingle();
    }

    [Fact]
    public void WithoutAddTwoFactorUI_TheLayoutMountsNoListener()
    {
        var cut = RenderLayout();

        cut.FindComponents<MultiFactorRequiredListener>().Should().BeEmpty(
            "without the module nothing subscribes, so the handler never reads a 403 body");
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // RendererInfo after every Services.Add call (it freezes the provider).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return RenderAs<MainLayout>(TestPrincipal.AuthenticatedUser(), p => p.Add(
            layout => layout.Body,
            builder => builder.AddContent(0, "Page body")));
    }
}
