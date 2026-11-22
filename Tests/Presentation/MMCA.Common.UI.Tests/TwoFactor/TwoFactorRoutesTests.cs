using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth.TwoFactor;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.TwoFactor;
using Moq;

namespace MMCA.Common.UI.Tests.TwoFactor;

/// <summary>
/// The shared router with and without the opt-in (design 2.2.4): a signed-in request for
/// <c>/profile/two-factor</c> renders the not-found page in a host that never called
/// <c>AddTwoFactorUI()</c>, and the account page in one that did.
/// </summary>
public sealed class TwoFactorRoutesTests : BunitTestBase
{
    private readonly Mock<ITwoFactorUIService> _twoFactor = new();

    public TwoFactorRoutesTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<ITokenStorageService>(new StubTokenStorageService());
        Services.AddSingleton<IOptions<LayoutSettings>>(Options.Create(new LayoutSettings { BrandName = "TestBrand" }));
        Services.AddSingleton(new Mock<IAppDialogService>().Object);

        // The page policy AddUIShared registers in every host.
        Services.AddAuthorizationCore(options => options.AddPolicy(
            TwoFactorPageRequirement.PolicyName,
            policy => policy.AddRequirements(new TwoFactorPageRequirement())));
        Services.AddSingleton<IAuthorizationHandler, TwoFactorPageAuthorizationHandler>();

        _twoFactor
            .Setup(t => t.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TwoFactorStatusResponse(false, 0)));
    }

    [Fact]
    public void WithoutAddTwoFactorUI_TheRouteRendersNotFound()
    {
        var cut = RenderRoutesAt("/profile/two-factor");

        cut.WaitForAssertion(() => cut.FindComponents<UI.Pages.NotFound>().Should().ContainSingle());
        cut.FindComponents<TwoFactorAccount>().Should().BeEmpty("a host that adopts nothing has no two-factor page");
    }

    [Fact]
    public void WithAddTwoFactorUI_TheRouteRendersTheAccountPage()
    {
        Services.AddSingleton(_twoFactor.Object);
        Services.AddTwoFactorUI();

        var cut = RenderRoutesAt("/profile/two-factor");

        cut.WaitForAssertion(() => cut.FindComponents<TwoFactorAccount>().Should().ContainSingle());
        cut.FindComponents<UI.Pages.NotFound>().Should().BeEmpty();
    }

    private IRenderedComponent<Routes> RenderRoutesAt(string uri)
    {
        // RendererInfo after every Services.Add call (it freezes the provider).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo(uri);
        SetUser(TestPrincipal.AuthenticatedUser());
        return Render<Routes>();
    }
}
