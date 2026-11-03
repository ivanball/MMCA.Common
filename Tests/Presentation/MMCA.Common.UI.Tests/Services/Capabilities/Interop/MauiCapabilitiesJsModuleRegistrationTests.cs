using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Maui.Capabilities.Interop;
using MMCA.Common.UI.Pages.Auth.SignInLinks;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Interop;

/// <summary>
/// D4 (Run 15): a MAUI head never called <c>AddBrowserDeviceCapabilities</c>, so nothing registered
/// <see cref="CapabilitiesJsModule"/> and the sign-in-link, confirm-email and reset-password pages read
/// a null fragment ("This link is incomplete."). <c>AddMauiDeviceCapabilities</c> now registers the
/// module through <see cref="MauiCapabilitiesJsModuleRegistration"/>, compiled here as linked source
/// because the MAUI package has no net10.0 target.
/// </summary>
public sealed class MauiCapabilitiesJsModuleRegistrationTests : BunitComponentTestBase
{
    private const string ModulePath = "./_content/MMCA.Common.UI/capabilities-interop.js";

    [Fact]
    public async Task MauiRegistration_ResolvesTheCapabilitiesJsModule()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IJSRuntime>());

        services.AddMauiCapabilitiesJsModule();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetService<CapabilitiesJsModule>().Should().NotBeNull();
    }

    [Fact]
    public void MauiRegistration_IsIdempotentWithTheBrowserRegistration()
    {
        var services = new ServiceCollection();

        services.AddBrowserDeviceCapabilities();
        services.AddMauiCapabilitiesJsModule();

        services.Count(d => d.ServiceType == typeof(CapabilitiesJsModule)).Should().Be(1);
    }

    [Fact]
    public void SignInLink_UnderTheMauiRegistration_ReadsEmailAndTokenFromTheFragment()
    {
        var signInLinks = new Mock<ISignInLinkUIService>();
        signInLinks
            .Setup(x => x.RequestSignInLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        Services.AddSingleton(signInLinks.Object);
        Services.AddSingleton(Options.Create(new SignInSettings { SignInLinkLandingPath = "/" }));
        Services.AddMauiCapabilitiesJsModule();

        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("locationHashTake", _ => true)
            .SetResult($"email={Uri.EscapeDataString("ada@example.com")}&token=tok-123");

        RenderMudProviders();
        var cut = RenderUnderTest<SignInLink>(_ => { });

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid=sign-in-link-address]").TextContent.Should().Contain("ada@example.com"));
    }
}
