using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="NotificationState"/>: unread-count mutation and change notification,
/// the refresh-request signal, the freshness stamp subscribers use to decide whether the count is
/// worth re-reading (§19), and the poller registration protocol that keeps a single
/// NotificationBell instance polling when the bell renders in multiple DOM locations.
/// </summary>
public sealed class NotificationStateTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly NotificationState _sut;

    public NotificationStateTests() => _sut = new NotificationState(_clock);

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

    // == Freshness stamp (§19) ==
    [Fact]
    public void LastFetchedUtc_StartsNull() =>
        _sut.LastFetchedUtc.Should().BeNull("no count has been established yet");

    [Fact]
    public void SetUnreadCount_WithANewValue_StampsLastFetched()
    {
        _sut.SetUnreadCount(5);

        _sut.LastFetchedUtc.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public void SetUnreadCount_WithTheSameValue_StillStampsLastFetched()
    {
        // The stamp answers "how old is this number", not "when did it change". On a quiet inbox
        // almost every read re-confirms the same count, and treating that as no read at all is what
        // would leave a subscriber permanently stale and re-fetching on every trigger.
        _sut.SetUnreadCount(5);
        _clock.Advance(TimeSpan.FromMinutes(5));

        _sut.SetUnreadCount(5);

        _sut.LastFetchedUtc.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public void IsStale_BeforeAnyFetch_IsTrue() =>
        _sut.IsStale(TimeSpan.FromSeconds(30)).Should().BeTrue();

    [Fact]
    public void IsStale_WithinTheWindow_IsFalse()
    {
        _sut.SetUnreadCount(3);

        _clock.Advance(TimeSpan.FromSeconds(29));

        _sut.IsStale(TimeSpan.FromSeconds(30)).Should().BeFalse();
    }

    [Fact]
    public void IsStale_PastTheWindow_IsTrue()
    {
        _sut.SetUnreadCount(3);

        _clock.Advance(TimeSpan.FromSeconds(31));

        _sut.IsStale(TimeSpan.FromSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void MarkStale_DiscardsTheStampWhateverTheClockSays()
    {
        // A real-time push says the data moved, so the age of the number is no longer evidence of
        // anything: the next read must happen even though the stamp is seconds old.
        _sut.SetUnreadCount(3);

        _sut.MarkStale();

        _sut.LastFetchedUtc.Should().BeNull();
        _sut.IsStale(TimeSpan.FromHours(1)).Should().BeTrue();
    }

    [Fact]
    public void IncrementUnreadCount_DoesNotStampLastFetched()
    {
        // The optimistic increment from a push is a guess, not an authoritative read; stamping it
        // would let the guess suppress the API read that confirms it.
        _sut.IncrementUnreadCount();

        _sut.LastFetchedUtc.Should().BeNull();
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
    // The slot is owner-based rather than counted: a counter leaked one increment per teardown that
    // never unregistered, and after the first leak no bell could ever win the slot again.
    [Fact]
    public void TryRegisterPoller_FirstOwnerClaimsSlot_SecondOwnerRejected()
    {
        var first = new object();
        var second = new object();

        _sut.TryRegisterPoller(first).Should().BeTrue("the first bell instance should start polling");
        _sut.TryRegisterPoller(second).Should().BeFalse("duplicate bell renders must not double-poll");
    }

    [Fact]
    public void TryRegisterPoller_SameOwnerTwice_StillHoldsTheSlot()
    {
        var owner = new object();

        _sut.TryRegisterPoller(owner).Should().BeTrue();
        _sut.TryRegisterPoller(owner).Should().BeTrue("re-claiming by the holder is idempotent");
    }

    [Fact]
    public void UnregisterPoller_ByTheOwner_FreesSlotAndRaisesOnPollerSlotFreed()
    {
        var owner = new object();
        var freed = 0;
        _sut.OnPollerSlotFreed += (_, _) => freed++;
        _sut.TryRegisterPoller(owner);

        _sut.UnregisterPoller(owner);

        freed.Should().Be(1);
        _sut.TryRegisterPoller(new object()).Should().BeTrue();
    }

    [Fact]
    public void UnregisterPoller_ByANonOwner_LeavesTheSlotHeld()
    {
        var owner = new object();
        var stranger = new object();
        var freed = 0;
        _sut.TryRegisterPoller(owner);
        _sut.OnPollerSlotFreed += (_, _) => freed++;

        _sut.UnregisterPoller(stranger);

        freed.Should().Be(0);
        _sut.TryRegisterPoller(new object()).Should().BeFalse("the live poller must not be evicted by a non-owner");
    }

    [Fact]
    public void UnregisterPoller_WhenSlotIsFree_DoesNotRaiseOnPollerSlotFreed()
    {
        var freed = 0;
        _sut.OnPollerSlotFreed += (_, _) => freed++;

        _sut.UnregisterPoller(new object());

        freed.Should().Be(0);
    }

    [Fact]
    public void TwoOwnersTornDownAndRebuilt_LeavesTheSlotClaimable()
    {
        // The regression: two bells inside <AuthorizeView> are torn down and rebuilt on every
        // authentication-state change (every access-token refresh). Each cycle must end with the
        // slot claimable, not leaked.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var desktop = new object();
            var mobile = new object();

            _sut.TryRegisterPoller(desktop).Should().BeTrue();
            _sut.TryRegisterPoller(mobile).Should().BeFalse();

            _sut.UnregisterPoller(desktop);
            _sut.UnregisterPoller(mobile);
        }

        _sut.TryRegisterPoller(new object()).Should().BeTrue("no teardown cycle may leak the slot");
    }

    [Fact]
    public void TryRegisterPoller_WithNullOwner_Throws()
    {
        var act = () => _sut.TryRegisterPoller(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
