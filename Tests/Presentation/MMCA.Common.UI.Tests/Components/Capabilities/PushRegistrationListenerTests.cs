using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="PushRegistrationListener"/> (ADR-044): on a head that supports push it registers
/// this device for a user already signed in at first render and again whenever the auth state turns
/// authenticated; where push is unsupported it stays inert; once disposed it stops listening.
/// </summary>
public sealed class PushRegistrationListenerTests : BunitTestBase
{
    private static readonly ClaimsPrincipal SignedIn =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ada")], "jwt"));

    private readonly Mock<IPushRegistrationService> _push = new();

    public PushRegistrationListenerTests()
    {
        _push.Setup(p => p.RegisterAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Services.AddSingleton(_push.Object);
    }

    [Fact]
    public void Supported_WithAUserAlreadySignedIn_RegistersOnFirstRender()
    {
        _push.SetupGet(p => p.IsSupported).Returns(true);

        RenderAs<PushRegistrationListener>(SignedIn, _ => { });

        _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void Supported_ForAnAnonymousVisitor_DoesNotRegister_UntilTheUserSignsIn()
    {
        _push.SetupGet(p => p.IsSupported).Returns(true);
        var cut = RenderUnderTest<PushRegistrationListener>(_ => { });

        _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Never());

        SetUser(SignedIn);

        cut.WaitForAssertion(() => _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Once()));
    }

    [Fact]
    public void Supported_ARerender_DoesNotRegisterAgain()
    {
        _push.SetupGet(p => p.IsSupported).Returns(true);
        var cut = RenderAs<PushRegistrationListener>(SignedIn, _ => { });

        cut.Render();

        _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void Unsupported_NeverRegisters_NorListensForSignIns()
    {
        _push.SetupGet(p => p.IsSupported).Returns(false);
        var cut = RenderAs<PushRegistrationListener>(SignedIn, _ => { });

        SetUser(Anonymous);
        SetUser(SignedIn);
        cut.Render();

        _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task AfterDispose_ASignInNoLongerRegisters()
    {
        _push.SetupGet(p => p.IsSupported).Returns(true);
        RenderUnderTest<PushRegistrationListener>(_ => { });
        await DisposeComponentsAsync();

        SetUser(SignedIn);

        _push.Verify(p => p.RegisterAsync(It.IsAny<CancellationToken>()), Times.Never());
    }
}
