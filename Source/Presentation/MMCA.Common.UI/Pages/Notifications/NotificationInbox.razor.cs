using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Code-behind for the notification inbox page.
/// Displays the current user's notifications with read/unread state and pagination.
/// Reloads the current page when a real-time push requests a refresh via
/// <see cref="NotificationState.OnRefreshRequested"/>.
/// </summary>
public partial class NotificationInbox : IDisposable
{
    private const int PageSize = 20;

    [Inject] private INotificationInboxUIService InboxService { get; set; } = default!;
    [Inject] private NotificationState NotificationState { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private string Title => L["Notif.Inbox.Title"].Value;

    private List<BreadcrumbItem> _breadcrumbs = [];

    protected bool IsLoading { get; private set; }
    protected bool IsSaving { get; private set; }

    private List<UserNotificationDTO> _notifications = [];
    private int _currentPage = 1;
    private int _totalPages = 1;

    /// <summary>A push arrived while a load was in flight and still owes the list one reload.</summary>
    private bool _refreshPending;

    protected override async Task OnInitializedAsync()
    {
        // Breadcrumbs are built here (not in a field initializer) so the injected localizer is
        // available; labels re-resolve per circuit and follow the active culture (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, "/", icon: Icons.Material.Filled.Home),
            new(L["Notif.Inbox.Title"].Value, href: null, disabled: true),
        ];

        NotificationState.OnRefreshRequested += HandleRefreshRequested;

        await LoadNotificationsAsync();
    }

    private void HandleRefreshRequested(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(RefreshFromPushAsync);
    }

    private async Task RefreshFromPushAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsLoading)
        {
            // Never drop the push: the in-flight load drains this flag when it completes, so
            // overlapping pushes coalesce into exactly one trailing reload instead of vanishing.
            _refreshPending = true;
            return;
        }

        await LoadNotificationsAsync();
        StateHasChanged();
    }

    private async Task LoadNotificationsAsync()
    {
        IsLoading = true;
        try
        {
            var result = await InboxService.GetInboxAsync(_currentPage, PageSize, _cts.Token);
            if (result.TryGetValue(out var page))
            {
                _notifications = [.. page.Items];
                _totalPages = (int)Math.Ceiling((double)page.PaginationMetadata.TotalItemCount / PageSize);
                if (_totalPages < 1)
                {
                    _totalPages = 1;
                }
            }
            else
            {
                // Same surface as the exception path it replaces: one snackbar, the list left as it
                // was rather than blanked, so a transient failure does not erase what is on screen.
                result.NotifyOnFailure(Snackbar, L);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        finally
        {
            IsLoading = false;
        }

        // Trailing reload for any push that arrived while the load above was in flight. The flag is
        // cleared first, so a push arriving during THIS reload queues one more and no further:
        // recursion is bounded by the pushes actually received, never self-sustaining.
        if (_refreshPending && !_disposed)
        {
            _refreshPending = false;
            await RefreshFromPushAsync();
        }
    }

    private async Task OnPageChangedAsync(int page)
    {
        _currentPage = page;
        await LoadNotificationsAsync();
    }

    private async Task MarkReadAsync(UserNotificationDTO notification)
    {
        IsSaving = true;
        try
        {
            var markRead = await InboxService.MarkReadAsync(notification.Id, _cts.Token);
            if (markRead.IsFailure)
            {
                markRead.NotifyOnFailure(Snackbar, L);
                return;
            }

            // Update local state
            int index = _notifications.FindIndex(n => n.Id == notification.Id);
            if (index >= 0)
            {
                _notifications[index] = notification with { IsRead = true, ReadOn = DateTime.UtcNow };
            }

            // Refresh the unread count; a failed count means "unknown", so the badge keeps its value.
            var count = await InboxService.GetUnreadCountAsync(_cts.Token);
            if (count.TryGetValue(out var unread))
            {
                NotificationState.SetUnreadCount(unread);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task MarkAllReadAsync()
    {
        IsSaving = true;
        try
        {
            var markAllRead = await InboxService.MarkAllReadAsync(_cts.Token);
            if (markAllRead.IsFailure)
            {
                markAllRead.NotifyOnFailure(Snackbar, L);
                return;
            }

            // Update local state
            for (int i = 0; i < _notifications.Count; i++)
            {
                if (!_notifications[i].IsRead)
                {
                    _notifications[i] = _notifications[i] with { IsRead = true, ReadOn = DateTime.UtcNow };
                }
            }

            NotificationState.SetUnreadCount(0);
            Snackbar.Add(L["Notif.AllMarkedRead"], Severity.Success);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        if (disposing)
        {
            NotificationState.OnRefreshRequested -= HandleRefreshRequested;
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
