using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Components.Notifications;

/// <summary>
/// Code-behind for the notification bell: renders the unread badge from the scoped
/// <see cref="NotificationState"/>, and the first rendered instance registers as the single active
/// poller (periodic + on-navigation refresh) so duplicate bell placements never duplicate API calls.
/// </summary>
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

        // Only the first instance registers as the active poller to prevent
        // duplicate API calls when NotificationBell renders in multiple DOM locations.
        _isActivePoller = State.TryRegisterPoller();
        if (_isActivePoller)
        {
            NavigationManager.LocationChanged += OnLocationChanged;
            await RefreshUnreadCountAsync();

            _pollTimer = new PeriodicTimer(PollInterval);
            _ = PollLoopAsync();
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
            int count = await InboxService.GetUnreadCountAsync(_cts.Token);
            if (!_disposed)
            {
                await InvokeAsync(() =>
                {
                    State.SetUnreadCount(count);
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
            // Network or deserialization error — badge stays at current value
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

        if (_isActivePoller)
        {
            NavigationManager.LocationChanged -= OnLocationChanged;
            State.UnregisterPoller();
        }

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
