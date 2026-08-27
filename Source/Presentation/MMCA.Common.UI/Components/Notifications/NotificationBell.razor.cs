using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Components.Notifications;

/// <summary>
/// Code-behind for the notification bell: renders the unread badge from the scoped
/// <see cref="NotificationState"/>, and one instance at a time holds the single active-poller slot
/// (periodic + on-navigation refresh) so duplicate bell placements never duplicate API calls.
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    [Inject] private NotificationState State { get; set; } = default!;
    [Inject] private INotificationInboxUIService InboxService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _pollTimer;
    private bool _isActivePoller;
    private bool _disposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        State.OnChange += HandleStateChanged;
        State.OnRefreshRequested += HandleRefreshRequested;
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
        await RefreshUnreadCountAsync();

        _pollTimer = new PeriodicTimer(PollInterval);
        _ = PollLoopAsync();
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

    private async Task PollLoopAsync()
    {
        try
        {
            while (await _pollTimer!.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshUnreadCountAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
    }

    // Event-handler signature; the refresh task observes its own failures internally (catch-all),
    // so the explicit discard is safe and avoids the async-void crash-the-process mode (VSTHRD100).
    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        _ = RefreshUnreadCountAsync();

    /// <summary>
    /// Called when NotificationListener receives a SignalR push and requests an API refresh.
    /// This provides a second chance to update the badge if the optimistic IncrementUnreadCount
    /// didn't trigger a re-render (e.g., cross-component InvokeAsync dispatch was dropped).
    /// </summary>
    private void HandleRefreshRequested(object? sender, EventArgs e) =>
        _ = RefreshUnreadCountAsync();

    private async Task RefreshUnreadCountAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var count = await InboxService.GetUnreadCountAsync(_cts.Token);
            if (!count.TryGetValue(out var unread))
            {
                // The authoritative count is unknown (expired session, transient failure). Leave the
                // badge exactly as it is: zeroing it here is what used to erase a push increment.
                // A failed read is silent by design; the bell has no surface to report it on.
                return;
            }

            if (!_disposed)
            {
                await InvokeAsync(() =>
                {
                    State.SetUnreadCount(unread);
                    StateHasChanged();
                });
            }
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
