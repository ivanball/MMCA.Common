using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Notifications;
using MMCA.Common.UI.Pages.Auth.TwoFactor;
using MMCA.Common.UI.TwoFactor;

namespace MMCA.Common.UI.Tests.TwoFactor;

/// <summary>
/// The router-side two-factor page gate (design 2.2.4, NotificationPageGate pattern): a host that never
/// called <c>AddTwoFactorUI()</c> must not serve the account page (its client is not registered), while a
/// host that registered the two-factor UI module keeps the route. The gate touches no other page.
/// </summary>
public sealed class TwoFactorPageGateTests
{
    [Fact]
    public void Hides_TheAccountPage_WhenNoModuleIsRegisteredAtAll() =>
        TwoFactorPageGate.Hides(typeof(TwoFactorAccount), []).Should().BeTrue(
            "a host without AddTwoFactorUI has no ITwoFactorUIService for the page");

    [Fact]
    public void Hides_TheAccountPage_WhenOnlyOtherModulesAreRegistered() =>
        TwoFactorPageGate.Hides(typeof(TwoFactorAccount), [new OtherModule(), new NotificationUIModule()]).Should().BeTrue(
            "another module's registration does not make the two-factor page routable");

    [Fact]
    public void KeepsTheAccountPage_WhenTheTwoFactorUIIsRegistered() =>
        TwoFactorPageGate.Hides(typeof(TwoFactorAccount), [new OtherModule(), new TwoFactorUIModule()]).Should().BeFalse(
            "a host that called AddTwoFactorUI() has every service the page needs");

    [Fact]
    public void KeepsAnyOtherPage_WithoutTheTwoFactorUI() =>
        TwoFactorPageGate.Hides(typeof(UI.Pages.Auth.Login), []).Should().BeFalse(
            "the sign-in page's code step needs no AddTwoFactorUI; the gate is scoped to the account page");

    private sealed class OtherModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public IReadOnlyList<Type> LayoutComponentTypes { get; } = [];

        public Assembly Assembly => typeof(OtherModule).Assembly;
    }
}
