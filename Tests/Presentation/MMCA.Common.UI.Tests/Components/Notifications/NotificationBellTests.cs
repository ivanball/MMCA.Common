using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Notifications;

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
/// surviving bell, and leaving the badge alone (silently) when the count cannot be established).
/// </summary>
public sealed class NotificationBellTests : BunitTestBase
{
    /// <summary>
    /// A poll interval far longer than any window the staleness tests advance through, so a
    /// <see cref="FakeTimeProvider.Advance"/> that ages the count never also fires the periodic tick
    /// and muddles the call count.
    /// </summary>
    private static readonly TimeSpan NoPollWithinTheTest = TimeSpan.FromHours(1);

    private readonly Mock<INotificationInboxUIService> _inbox = new();
    private readonly Mock<IToastService> _toast = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly NotificationState _state;

    public NotificationBellTests()
    {
        _state = new NotificationState(_clock);
        Services.AddSingleton(_state);
        Services.AddSingleton(_inbox.Object);

        // Registered after the base harness's TimeProvider.System, so the bell's periodic timer and
        // the state's freshness stamp both run off the same driveable clock (last registration wins).
        Services.AddSingleton<TimeProvider>(_clock);

        // Registered after the base class's default facade, so this wins: the bell has no error
        // surface of its own, and this is how a stray toast from it would be caught.
        Services.AddSingleton<IToastService>(_toast.Object);
    }

    /// <summary>
    /// Pins the bell's staleness policy for one test; call before rendering. Registered as the closed
    /// <c>IOptions&lt;T&gt;</c> rather than through <c>Configure</c> because the settings properties are
    /// init-only, matching the other settings classes in the framework.
    /// </summary>
    private void BellOptions(TimeSpan navigationRefreshMaxAge, TimeSpan? pollInterval = null) =>
        Services.AddSingleton<IOptions<NotificationBellOptions>>(
            Options.Create(new NotificationBellOptions
            {
                NavigationRefreshMaxAge = navigationRefreshMaxAge,
                PollInterval = pollInterval ?? NoPollWithinTheTest,
            }));

    private void VerifyFetchCount(Times times) =>
        _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), times);

    private void CountIs(int count) =>
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(count));

    // A failed read means the authoritative count is UNKNOWN (expired session, transient failure),
    // which replaced the old null int?.
    private void CountIsUnavailable() =>
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<int>(Error.Unauthorized("Notif.Count.Unauthorized", "Your session expired.")));

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
    public async Task DisposingWhileTheFirstReadIsInFlight_DoesNotStartThePollTimer()
    {
        // BecomeActivePollerAsync checked _disposed only on entry, then awaited the first read. A
        // teardown during that await left it creating a PeriodicTimer nothing would ever dispose and
        // starting a loop whose first act is to read an already-disposed CancellationTokenSource.
        var counting = new TimerCountingTimeProvider(_clock);
        Services.AddSingleton<TimeProvider>(counting);

        var gate = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);

        RenderUnderTest<NotificationBell>(_ => { });
        counting.TimersCreated.Should().Be(0, "the first read has not returned yet");

        await DisposeComponentsAsync();
        gate.SetResult(Result.Success(3));
        await Task.Yield();

        counting.TimersCreated.Should().Be(0, "a disposed bell must not start a poll loop");
        VerifyFetchCount(Times.Once());
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
        // A failed count means "unknown" (expired session, transient failure). Zeroing the badge here
        // is what erased the increment a real-time push had just applied.
        CountIsUnavailable();
        _state.SetUnreadCount(4);

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        _state.UnreadCount.Should().Be(4);
        cut.Markup.Should().Contain("4");
    }

    [Fact]
    public void WhenTheCountIsUnavailable_TheBellStaysSilent()
    {
        // The bell is chrome: it has nowhere to report a failure, and a toast on every failed poll
        // would fire every 30 seconds for the life of an expired session.
        CountIsUnavailable();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce()));

        _toast.VerifyNoOtherCalls();
    }

    // == Staleness policy (§19) ==
    [Fact]
    public void NavigatingWhileTheCountIsFresh_DoesNotRefetch()
    {
        // Navigation is an ambient trigger, not evidence the count moved. A user clicking through a
        // menu used to issue one API read per click for a number that had not changed.
        CountIs(2);
        BellOptions(navigationRefreshMaxAge: TimeSpan.FromSeconds(30));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() => VerifyFetchCount(Times.Once()));

        _clock.Advance(TimeSpan.FromSeconds(10));
        nav.NavigateTo("/somewhere");
        nav.NavigateTo("/somewhere-else");

        VerifyFetchCount(Times.Once());
    }

    [Fact]
    public void NavigatingOnceTheCountIsStale_Refetches()
    {
        CountIs(2);
        BellOptions(navigationRefreshMaxAge: TimeSpan.FromSeconds(30));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() => VerifyFetchCount(Times.Once()));

        _clock.Advance(TimeSpan.FromSeconds(31));
        nav.NavigateTo("/somewhere");

        cut.WaitForAssertion(() => VerifyFetchCount(Times.Exactly(2)));
    }

    [Fact]
    public void NavigatingAfterAFailedRead_Refetches()
    {
        // A failed read never established a count, so nothing stamped it fresh: the next navigation
        // must try again rather than sit on an unknown value for the whole window.
        CountIsUnavailable();
        BellOptions(navigationRefreshMaxAge: TimeSpan.FromHours(1));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() => VerifyFetchCount(Times.Once()));

        nav.NavigateTo("/somewhere");

        cut.WaitForAssertion(() => VerifyFetchCount(Times.Exactly(2)));
    }

    [Fact]
    public void APushRefresh_MarksTheCountStaleAndRefetchesInsideTheWindow()
    {
        // The server has just said the data changed, so the age of the number carries no information:
        // this path must never be throttled by the navigation window.
        CountIs(2);
        BellOptions(navigationRefreshMaxAge: TimeSpan.FromHours(1));

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() => VerifyFetchCount(Times.Once()));
        _state.LastFetchedUtc.Should().NotBeNull();

        _state.RequestRefresh();

        cut.WaitForAssertion(() => VerifyFetchCount(Times.Exactly(2)));
    }

    [Fact]
    public void ThePeriodicTick_RefetchesUnconditionally()
    {
        // The tick runs off the injected TimeProvider (the PeriodicTimer overload), so the poll is
        // asserted directly rather than inferred: advancing one interval produces one read even
        // though the count is seconds old and a navigation would have skipped it.
        CountIs(2);
        BellOptions(navigationRefreshMaxAge: TimeSpan.FromHours(1), pollInterval: TimeSpan.FromSeconds(30));

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        cut.WaitForAssertion(() => VerifyFetchCount(Times.Once()));

        _clock.Advance(TimeSpan.FromSeconds(30));

        cut.WaitForAssertion(() => VerifyFetchCount(Times.Exactly(2)));
    }

    [Fact]
    public void WhenTheCountRecovers_TheBadgeCatchesUp()
    {
        // "Unknown" is not a terminal state: the next successful read is authoritative again.
        _inbox
            .SetupSequence(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<int>(Error.Unauthorized("Notif.Count.Unauthorized", "Your session expired.")))
            .ReturnsAsync(Result.Success(7));

        var cut = RenderUnderTest<NotificationBell>(_ => { });
        // Wait for the failed first read before pushing the refresh, so the order is deterministic.
        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.Once()));

        _state.RequestRefresh();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("7"));
        _state.UnreadCount.Should().Be(7);
    }

    /// <summary>
    /// Delegates the clock to the driveable <see cref="FakeTimeProvider"/> and counts timer
    /// creations: <c>new PeriodicTimer(TimeSpan, TimeProvider)</c> goes through
    /// <see cref="TimeProvider.CreateTimer"/>, so the count is how a test observes whether the poll
    /// loop was ever started.
    /// </summary>
    private sealed class TimerCountingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersCreated);
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
