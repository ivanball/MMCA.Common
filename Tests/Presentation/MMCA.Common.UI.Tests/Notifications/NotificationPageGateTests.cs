using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Notifications;
using MMCA.Common.UI.Pages.Notifications;

namespace MMCA.Common.UI.Tests.Notifications;

/// <summary>
/// The router-side gate behind <see cref="LayoutSettings.HideNotificationPagesWhenUnregistered"/>:
/// a host that opted in and never called <c>AddNotificationUI()</c> must not serve the notification
/// pages, while the default and every host that registered the notification UI keep today's routes.
/// </summary>
public sealed class NotificationPageGateTests
{
    private static readonly LayoutSettings OptedIn = new() { HideNotificationPagesWhenUnregistered = true };

    public static TheoryData<Type> NotificationPages =>
        [typeof(NotificationList), typeof(NotificationInbox), typeof(NotificationSend)];

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void Hides_ANotificationPage_WhenOptedIn_AndNoNotificationUIIsRegistered(Type page) =>
        NotificationPageGate.Hides(page, OptedIn, [new OtherModule()]).Should().BeTrue(
            "the host asked for the pages to disappear when it registered no notification services");

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void KeepsANotificationPage_ByDefault_EvenWithNoNotificationUI(Type page) =>
        NotificationPageGate.Hides(page, new LayoutSettings(), []).Should().BeFalse(
            "the default must leave every route exactly as it was");

    [Theory]
    [MemberData(nameof(NotificationPages))]
    public void KeepsANotificationPage_WhenTheNotificationUIIsRegistered(Type page) =>
        NotificationPageGate.Hides(page, OptedIn, [new OtherModule(), new NotificationUIModule()]).Should().BeFalse(
            "a host that called AddNotificationUI() has every service the pages need");

    [Fact]
    public void KeepsAnyOtherPage_WhenOptedIn() =>
        NotificationPageGate.Hides(typeof(UI.Pages.NotFound), OptedIn, []).Should().BeFalse(
            "the option is scoped to the notification pages and nothing else");

    private sealed class OtherModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public IReadOnlyList<Type> AppBarComponentTypes { get; } = [];

        public IReadOnlyList<Type> LayoutComponentTypes { get; } = [];

        public Assembly Assembly => typeof(OtherModule).Assembly;
    }
}
