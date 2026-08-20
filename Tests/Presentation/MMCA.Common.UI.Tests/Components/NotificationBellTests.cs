using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Host that renders the bell in the two placements a real layout uses (desktop app bar and mobile
/// nav), each independently removable, so a test can tear one down the way an
/// <c>&lt;AuthorizeView&gt;</c> rebuild does. Keys keep the diff from reusing one instance for the other slot.
/// </summary>
internal sealed class NotificationBellHost : ComponentBase
{
    [Parameter] public bool ShowFirst { get; set; } = true;

    [Parameter] public bool ShowSecond { get; set; } = true;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (ShowFirst)
        {
            builder.OpenComponent<NotificationBell>(0);
            builder.SetKey("first");
            builder.CloseComponent();
        }

        if (ShowSecond)
        {
            builder.OpenComponent<NotificationBell>(1);
            builder.SetKey("second");
            builder.CloseComponent();
        }
    }
}

/// <summary>
/// bUnit tests for <see cref="NotificationBell"/>: badge count, navigation on click, reaction to
/// shared-state changes, and the single-active-poller protocol (symmetric registration, takeover by a
/// surviving bell, and leaving the badge alone when the count cannot be established).
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

    private void CountIs(int? count) =>
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(count);

    [Fact]
    public void RendersUnreadCount_FromService()
    {
        CountIs(5);

        var cut = RenderUnderTest<NotificationBell>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("5"));
    }

    [Fact]
    public void ClickingBell_NavigatesToInbox()
    {
        CountIs(0);
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.Find("button").Click();

        nav.Uri.Should().EndWith("/notifications/inbox");
    }

    [Fact]
    public void WhenSharedStateChanges_BadgeReflectsNewCount()
    {
        CountIs(0);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        // SetUnreadCount raises OnChange; the bell's handler marshals StateHasChanged onto the renderer.
        _state.SetUnreadCount(3);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3"));
    }

    [Fact]
    public void OnlyTheFirstBell_BecomesActivePoller()
    {
        CountIs(0);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        // Wait until first-render registration + the initial API refresh have run.
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        // The bell holds the single active-poller slot, so another claimant is rejected.
        _state.TryRegisterPoller(new object()).Should().BeFalse();
    }

    [Fact]
    public async Task DisposingTheBell_AlwaysReleasesThePollerSlot()
    {
        // The staleness regression: the slot leaked on every teardown, after which no bell ever
        // polled again for the life of the circuit.
        CountIs(0);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        await cut.WaitForAssertionAsync(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        await DisposeComponentsAsync();

        _state.TryRegisterPoller(new object()).Should().BeTrue("a disposed bell must hand the slot back");
    }

    [Fact]
    public void WhenTheActiveBellIsTornDown_TheSurvivingBellTakesOverPolling()
    {
        CountIs(0);

        var cut = RenderUnderTest<NotificationBellHost>(_ => { });
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.Once()));

        // Tear down the placement that holds the slot, exactly as an AuthorizeView rebuild does.
        cut.Render(p => p.Add(x => x.ShowFirst, false));

        // The survivor claims the freed slot and refreshes the badge immediately.
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));
        _state.TryRegisterPoller(new object()).Should()
            .BeFalse("the surviving bell should now hold the active-poller slot");
    }

    [Fact]
    public void WhenTheCountIsUnavailable_TheBadgeKeepsItsValue()
    {
        // A null count means "unknown" (expired session, transient failure). Zeroing the badge here
        // is what erased the increment a real-time push had just applied.
        CountIs(null);
        _state.SetUnreadCount(4);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        _state.UnreadCount.Should().Be(4);
        cut.Markup.Should().Contain("4");
    }
}
