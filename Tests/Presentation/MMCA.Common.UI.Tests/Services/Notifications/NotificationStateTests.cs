using AwesomeAssertions;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="NotificationState"/>: unread-count mutation and change notification,
/// the refresh-request signal, and the poller registration protocol that keeps a single
/// NotificationBell instance polling when the bell renders in multiple DOM locations.
/// </summary>
public sealed class NotificationStateTests
{
    private readonly NotificationState _sut = new();

    // == Unread count ==
    [Fact]
    public void SetUnreadCount_WithNewValue_UpdatesCountAndRaisesOnChange()
    {
        var raised = 0;
        _sut.OnChange += (_, _) => raised++;

        _sut.SetUnreadCount(5);

        _sut.UnreadCount.Should().Be(5);
        raised.Should().Be(1);
    }

    [Fact]
    public void SetUnreadCount_WithSameValue_DoesNotRaiseOnChange()
    {
        _sut.SetUnreadCount(5);
        var raised = 0;
        _sut.OnChange += (_, _) => raised++;

        _sut.SetUnreadCount(5);

        raised.Should().Be(0);
    }

    [Fact]
    public void SetUnreadCount_WithNoSubscribers_DoesNotThrow()
    {
        var act = () => _sut.SetUnreadCount(3);

        act.Should().NotThrow();
    }

    [Fact]
    public void IncrementUnreadCount_IncrementsByOneAndRaisesOnChange()
    {
        _sut.SetUnreadCount(2);
        var raised = 0;
        _sut.OnChange += (_, _) => raised++;

        _sut.IncrementUnreadCount();

        _sut.UnreadCount.Should().Be(3);
        raised.Should().Be(1);
    }

    // == Refresh request ==
    [Fact]
    public void RequestRefresh_RaisesOnRefreshRequestedWithoutTouchingCount()
    {
        _sut.SetUnreadCount(4);
        var refreshRequested = 0;
        var changed = 0;
        _sut.OnRefreshRequested += (_, _) => refreshRequested++;
        _sut.OnChange += (_, _) => changed++;

        _sut.RequestRefresh();

        refreshRequested.Should().Be(1);
        changed.Should().Be(0);
        _sut.UnreadCount.Should().Be(4);
    }

    [Fact]
    public void RequestRefresh_WithNoSubscribers_DoesNotThrow()
    {
        var act = () => _sut.RequestRefresh();

        act.Should().NotThrow();
    }

    // == Poller registration ==
    [Fact]
    public void TryRegisterPoller_FirstCallerBecomesActive_SubsequentCallersDoNot()
    {
        _sut.TryRegisterPoller().Should().BeTrue("the first bell instance should start polling");
        _sut.TryRegisterPoller().Should().BeFalse("duplicate bell renders must not double-poll");
    }

    [Fact]
    public void TryRegisterPoller_AfterAllPollersUnregister_NextCallerBecomesActive()
    {
        _sut.TryRegisterPoller();
        _sut.UnregisterPoller();

        _sut.TryRegisterPoller().Should().BeTrue();
    }

    [Fact]
    public void TryRegisterPoller_WhileAnotherPollerRemainsRegistered_ReturnsFalse()
    {
        // Two bells registered, one leaves: the survivor is still counted, so a newcomer must not
        // become a second active poller.
        _sut.TryRegisterPoller();
        _sut.TryRegisterPoller();
        _sut.UnregisterPoller();

        _sut.TryRegisterPoller().Should().BeFalse();
    }
}
