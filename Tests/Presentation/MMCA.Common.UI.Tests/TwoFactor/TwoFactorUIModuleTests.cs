using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.TwoFactor;

namespace MMCA.Common.UI.Tests.TwoFactor;

/// <summary>
/// What <c>AddTwoFactorUI()</c> contributes (design 2.2.4): the client, one module whose single nav item
/// is <c>Nav.TwoFactor</c> in the user section pointing at the account page, and the listener as its one
/// layout component. Calling it twice registers the module once, so the nav item and the listener never
/// render twice. The nav key is declared in English and Spanish, or the menu would show the raw key.
/// </summary>
public sealed class TwoFactorUIModuleTests
{
    public static TheoryData<string> Cultures => [string.Empty, "es"];

    [Fact]
    public void TheModule_ContributesOneUserSectionNavItem_ToTheAccountPage()
    {
        var item = new TwoFactorUIModule().NavItems.Should().ContainSingle().Subject;

        item.Title.Should().Be("Nav.TwoFactor");
        item.Href.Should().Be("/profile/two-factor");
        item.Section.Should().Be(NavSection.User);
        TwoFactorUIModule.AccountPath.Should().Be("/profile/two-factor");
    }

    [Fact]
    public void TheModule_PutsTheListenerInTheLayout() =>
        new TwoFactorUIModule().LayoutComponentTypes.Should().Equal(typeof(MultiFactorRequiredListener));

    [Fact]
    public void AddTwoFactorUI_RegistersTheClientAndTheModule()
    {
        var services = new ServiceCollection();

        services.AddTwoFactorUI();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ITwoFactorUIService)
            && d.ImplementationType == typeof(TwoFactorUIService)
            && d.Lifetime == ServiceLifetime.Scoped);
        services.Where(d => d.ServiceType == typeof(IUIModule)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<TwoFactorUIModule>();
    }

    [Fact]
    public void AddTwoFactorUI_Twice_RegistersTheModuleOnce()
    {
        var services = new ServiceCollection();

        services.AddTwoFactorUI();
        services.AddTwoFactorUI();

        services.Where(d => d.ServiceType == typeof(IUIModule)).Should().ContainSingle(
            "a second module would render the nav item and the listener twice");
        services.Where(d => d.ServiceType == typeof(ITwoFactorUIService)).Should().ContainSingle();
    }

    [Fact]
    public void AddTwoFactorUI_KeepsAClientTheHostRegisteredFirst()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITwoFactorUIService, HostTwoFactorClient>();

        services.AddTwoFactorUI();

        services.Where(d => d.ServiceType == typeof(ITwoFactorUIService)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<HostTwoFactorClient>();
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TheNavTitle_IsDeclaredInItsResource(string culture)
    {
        var item = new TwoFactorUIModule().NavItems.Single();
        var resources = new ResourceManager(item.TitleResource)
            .GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false);

        resources.Should().NotBeNull();
        resources!.GetString(item.Title).Should().NotBeNullOrWhiteSpace(
            "the nav key must be declared for culture '{0}', or the menu shows the raw key",
            culture);
    }

    private sealed class HostTwoFactorClient : ITwoFactorUIService
    {
        public Task<Result<TwoFactorStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TwoFactorSetupResponse>> BeginEnrollmentAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TwoFactorRecoveryCodesResponse>> ConfirmEnrollmentAsync(string code, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TwoFactorRecoveryCodesResponse>> RegenerateRecoveryCodesAsync(string code, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DisableAsync(string code, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
