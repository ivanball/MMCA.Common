using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Notifications;
using MMCA.Common.UI.Pages.Notifications;

namespace MMCA.Common.UI.Tests.Notifications;

/// <summary>
/// The router-side notification page gate: a host that never called <c>AddNotificationUI()</c> must not
/// serve the notification pages (they need services it never registered), while every host that
/// registered the notification UI keeps the routes.
/// </summary>
public sealed class NotificationPageGateTests
{
    public static TheoryData<Type> NotificationPages =>
        [typeof(NotificationList), typeof(NotificationInbox), typeof(NotificationSend)];

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void Hides_ANotificationPage_WhenNoNotificationUIIsRegistered(Type page) =>
        NotificationPageGate.Hides(page, [new OtherModule()]).Should().BeTrue(
            "a host without the notification services must not serve pages that need them");

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void Hides_ANotificationPage_WhenNoModuleIsRegisteredAtAll(Type page) =>
        NotificationPageGate.Hides(page, []).Should().BeTrue(
            "there is no option to keep the pages routable without their services");

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void KeepsANotificationPage_WhenTheNotificationUIIsRegistered(Type page) =>
        NotificationPageGate.Hides(page, [new OtherModule(), new NotificationUIModule()]).Should().BeFalse(
            "a host that called AddNotificationUI() has every service the pages need");

    [Fact]
    public void KeepsAnyOtherPage_WithoutTheNotificationUI() =>
        NotificationPageGate.Hides(typeof(UI.Pages.NotFound), []).Should().BeFalse(
            "the gate is scoped to the notification pages and nothing else");

    private sealed class OtherModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public IReadOnlyList<Type> AppBarComponentTypes { get; } = [];

        public IReadOnlyList<Type> LayoutComponentTypes { get; } = [];

        public Assembly Assembly => typeof(OtherModule).Assembly;
    }
}
