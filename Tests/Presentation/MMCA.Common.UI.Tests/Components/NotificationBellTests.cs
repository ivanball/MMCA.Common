using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="NotificationBell"/> — badge count, navigation on click, reaction to
/// shared-state changes, and the single-active-poller registration that prevents duplicate polling.
/// </summary>
public sealed class NotificationBellTests : BunitTestBase
{
    private readonly Mock<INotificationInboxUIService> _inbox = new();
    private readonly NotificationState _state = new();

    public NotificationBellTests()
    {
        Services.AddSingleton(_state);
        Services.AddSingleton(_inbox.Object);
    }

    [Fact]
    public void RendersUnreadCount_FromService()
    {
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var cut = RenderUnderTest<NotificationBell>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("5"));
    }

    [Fact]
    public void ClickingBell_NavigatesToInbox()
    {
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.Find("button").Click();

        nav.Uri.Should().EndWith("/notifications/inbox");
    }

    [Fact]
    public void WhenSharedStateChanges_BadgeReflectsNewCount()
    {
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        // SetUnreadCount raises OnChange; the bell's handler marshals StateHasChanged onto the renderer.
        _state.SetUnreadCount(3);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3"));
    }

    [Fact]
    public void OnlyTheFirstBell_BecomesActivePoller()
    {
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        // Wait until first-render registration + the initial API refresh have run.
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        // The bell holds the single active-poller slot, so a second registration is rejected.
        _state.TryRegisterPoller().Should().BeFalse();
        _state.UnregisterPoller();
    }
}
