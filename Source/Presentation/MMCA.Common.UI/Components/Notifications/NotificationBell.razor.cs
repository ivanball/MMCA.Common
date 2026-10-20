using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Components.Notifications;

/// <summary>
/// Code-behind for the notification bell: renders the unread badge from the scoped
/// <see cref="NotificationState"/>, and one instance at a time holds the single active-poller slot
/// (periodic + on-navigation refresh) so duplicate bell placements never duplicate API calls.
/// <para>
/// Its staleness policy is explicit and configurable (<see cref="NotificationBellOptions"/>): the
/// first read and the periodic tick are unconditional, a navigation reads only when the count is
/// older than the configured window, and a real-time push marks the count stale and reads
/// regardless. Both the timer and the age comparison run off the injected <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// The periodic tick runs only while the session is accepted: an Unauthorized read stops it, and the
/// next successful read (navigation or push) starts it again.
/// </para>
/// </summary>
/// <remarks>
/// Hosts commonly render the bell twice (a desktop app bar and a mobile nav) inside
/// <c>&lt;AuthorizeView&gt;</c>, which tears the children down and rebuilds them on every
/// authentication-state change, including a routine access-token refresh. Registration is therefore
/// strictly symmetric (every instance unregisters on dispose, whether or not it was polling) and the
/// surviving instance takes the slot over through <see cref="NotificationState.OnPollerSlotFreed"/>,
/// so the circuit never ends up with a badge that no one refreshes.
/// </remarks>
public partial class NotificationBell : IDisposable
{
    [Inject] private NotificationState State { get; set; } = default!;
    [Inject] private INotificationInboxUIService InboxService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private IOptions<NotificationBellOptions> Options { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _pollTimer;
    private bool _isActivePoller;
    private bool _disposed;

    // Bumped by every unread-count read (L188): the poll, navigation and push reads overlap freely,
    // and only the newest one may write the badge.
    private int _readSequence;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        State.OnChange += HandleStateChanged;
        State.OnPollerSlotFreed += HandlePollerSlotFreed;

        // Only the slot holder polls, so duplicate bell placements do not duplicate API calls.
        if (State.TryRegisterPoller(this))
        {
            await BecomeActivePollerAsync();
        }
    }

    /// <summary>
    /// Starts this instance's polling once it holds the slot: an immediate authoritative refresh,
    /// then the periodic timer and the on-navigation refresh. Runs on the renderer's synchronization
    /// context (first render, or marshalled by <see cref="TryTakeOverPollingAsync"/>), which is what
    /// makes the <see cref="_isActivePoller"/> double-start guard sufficient.
    /// </summary>
    private async Task BecomeActivePollerAsync()
    {
        if (_disposed || _isActivePoller)
        {
            return;
        }

        _isActivePoller = true;
        NavigationManager.LocationChanged += OnLocationChanged;

        // Push refreshes belong to the slot holder, like the navigation refresh above: subscribing every
        // bell made each push read the API once per placement. A takeover runs this method again, so
        // the surviving bell picks the subscription up with the slot; Dispose unsubscribes.
        State.OnRefreshRequested += HandleRefreshRequested;

        // The first read is unconditional: nothing has established the count yet.
        var sessionRejected = await RefreshUnreadCountAsync();

        // Then the periodic backstop starts, even after a transient failure (retrying is its job),
        // but not when the server rejected the session: a dead session never starts a timer at all.
        if (!sessionRejected)
        {
            StartPollTimerIfStopped();
        }
    }

    /// <summary>
    /// Starts the periodic backstop when this bell holds the slot and no timer is running. Called
    /// after the first read and after every successful one, on the renderer's synchronization
    /// context, which is what makes
    /// the null check a sufficient double-start guard, and what restarts polling after
    /// <see cref="StopPollTimer"/> once a navigation or push read proves the session is back.
    /// </summary>
    private void StartPollTimerIfStopped()
    {
        // _disposed: Dispose may have run while the read was in flight, and it has already disposed
        // the (then null) timer and the token source. Starting the loop now would leak a PeriodicTimer
        // nothing disposes and fault the discarded task on a disposed _cts.
        if (_disposed || !_isActivePoller || _pollTimer is not null)
        {
            return;
        }

        // A zero or negative interval switches the periodic backstop off (push and navigation refresh
        // keep working). PeriodicTimer rejects such a period, and throwing here faulted the circuit.
        if (Options.Value.PollInterval <= TimeSpan.Zero)
        {
            return;
        }

        // The TimeProvider overload rather than the ambient system clock, so a test drives the loop
        // deterministically instead of waiting out a real interval.
        _pollTimer = new PeriodicTimer(Options.Value.PollInterval, Clock);
        _ = PollLoopAsync(_pollTimer);
    }

    /// <summary>
    /// Stops the periodic backstop. Disposing the timer makes the loop's pending
    /// <see cref="PeriodicTimer.WaitForNextTickAsync"/> answer false, so the loop ends on its own.
    /// </summary>
    private void StopPollTimer()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    // Event-handler signature; the takeover task observes its own failures internally, so the
    // explicit discard is safe and avoids the async-void crash-the-process mode (VSTHRD100).
    private void HandlePollerSlotFreed(object? sender, EventArgs e) =>
        _ = TryTakeOverPollingAsync();

    /// <summary>
    /// Claims the freed slot and starts polling. Raised synchronously from the disposing bell's
    /// thread, so the actual start is marshalled onto this component's renderer.
    /// </summary>
    private async Task TryTakeOverPollingAsync()
    {
        if (_disposed || _isActivePoller || !State.TryRegisterPoller(this))
        {
            return;
        }

        try
        {
            await InvokeAsync(BecomeActivePollerAsync);
        }
        catch (ObjectDisposedException)
        {
            // Disposed during the dispatch: hand the slot straight back so another bell can claim it.
            State.UnregisterPoller(this);
        }
    }

    private async Task PollLoopAsync(PeriodicTimer pollTimer)
    {
        try
        {
            while (await pollTimer.WaitForNextTickAsync(_cts.LifetimeToken()))
            {
                await RefreshUnreadCountAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the timer creation and the first wait: reading Token off the disposed source throws
            // rather than cancelling. Nothing to observe, and this task is discarded.
        }
    }

    /// <summary>
    /// Navigation is an ambient trigger, not evidence that the count moved: it fires on every page
    /// change, and a user clicking through a menu used to issue one API read per click for a number
    /// that had not changed. The read now happens only when the count is actually old enough
    /// (<see cref="NotificationBellOptions.NavigationRefreshMaxAge"/>).
    /// </summary>
    /// <remarks>
    /// Event-handler signature; the refresh task observes its own failures internally (catch-all),
    /// so the explicit discard is safe and avoids the async-void crash-the-process mode (VSTHRD100).
    /// </remarks>
    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        if (State.IsStale(Options.Value.NavigationRefreshMaxAge))
        {
            _ = RefreshUnreadCountAsync();
        }
    }

    /// <summary>
    /// Called when NotificationListener receives a SignalR push and requests an API refresh.
    /// This provides a second chance to update the badge if the optimistic IncrementUnreadCount
    /// didn't trigger a re-render (e.g., cross-component InvokeAsync dispatch was dropped).
    /// <para>
    /// Unconditional, and it marks the count stale first: the server has just said the data changed,
    /// so the age of the number carries no information any more. This is the one path that must never
    /// be throttled by the navigation window.
    /// </para>
    /// </summary>
    private void HandleRefreshRequested(object? sender, EventArgs e)
    {
        State.MarkStale();
        _ = RefreshUnreadCountAsync();
    }

    /// <summary>Reads the authoritative count; answers true only when the server rejected the session.</summary>
    private async Task<bool> RefreshUnreadCountAsync()
    {
        if (_disposed)
        {
            return false;
        }

        var sequence = Interlocked.Increment(ref _readSequence);
        try
        {
            var count = await InboxService.GetUnreadCountAsync(_cts.LifetimeToken());

            // A newer read started while this one was in flight: its answer is the authoritative one,
            // and this older one would overwrite it (and re-stamp it as fresh).
            if (sequence != Volatile.Read(ref _readSequence))
            {
                return false;
            }

            if (!count.TryGetValue(out var unread))
            {
                // The authoritative count is unknown (expired session, transient failure). Leave the
                // badge exactly as it is: zeroing it here is what used to erase a push increment.
                // A failed read is silent by design; the bell has no surface to report it on.
                if (count.IsUnauthorized() && !_disposed)
                {
                    // The service has already tried a forced token refresh and replay before it
                    // answers Unauthorized, so the session is gone. Stop the unattended backstop:
                    // left running, it re-read with the dead token (and re-tried the refresh) every
                    // interval for as long as the tab stayed open. Navigation and push reads still
                    // run, and the first one that succeeds restarts polling.
                    await InvokeAsync(StopPollTimer);
                    return true;
                }

                return false;
            }

            if (!_disposed)
            {
                await InvokeAsync(() =>
                {
                    if (sequence != Volatile.Read(ref _readSequence))
                    {
                        return;
                    }

                    State.SetUnreadCount(unread);
                    StartPollTimerIfStopped();
                    StateHasChanged();
                });
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        catch (ObjectDisposedException)
        {
            // Component was disposed during the async gap
        }
        catch
        {
            // Network or deserialization error - badge stays at current value
        }

        return false;
    }

    private void HandleStateChanged(object? sender, EventArgs e) =>
        _ = RerenderSafeAsync();

    private async Task RerenderSafeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // Component was disposed between event firing and render dispatch
        }
    }

    private void NavigateToInbox() => NavigationManager.NavigateTo(NotificationRoutePaths.NotificationInbox);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        State.OnChange -= HandleStateChanged;
        State.OnRefreshRequested -= HandleRefreshRequested;
        State.OnPollerSlotFreed -= HandlePollerSlotFreed;
        NavigationManager.LocationChanged -= OnLocationChanged;

        // Unconditional, and the slot is only released when this instance actually holds it: a bell
        // that claimed the slot but was torn down before it started polling still frees it, and a
        // bell that never held it cannot evict the live poller.
        State.UnregisterPoller(this);

        _pollTimer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
