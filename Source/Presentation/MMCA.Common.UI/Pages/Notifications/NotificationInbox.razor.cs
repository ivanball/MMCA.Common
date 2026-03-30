using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Code-behind for the notification inbox page.
/// Displays the current user's notifications with read/unread state and pagination.
/// </summary>
public partial class NotificationInbox : IDisposable
{
    private const int PageSize = 20;

    [Inject] private INotificationInboxUIService InboxService { get; set; } = default!;
    [Inject] private NotificationState NotificationState { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private static string Title => "Notifications";

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new("Home", "/", icon: Icons.Material.Filled.Home),
        new("Notifications", href: null, disabled: true),
    ];

    protected bool IsLoading { get; private set; }
    protected bool IsSaving { get; private set; }

    private List<UserNotificationDTO> _notifications = [];
    private int _currentPage = 1;
    private int _totalPages = 1;

    protected override async Task OnInitializedAsync() => await LoadNotificationsAsync();

    private async Task LoadNotificationsAsync()
    {
        IsLoading = true;
        try
        {
            var result = await InboxService.GetInboxAsync(_currentPage, PageSize, _cts.Token);
            if (result is not null)
            {
                _notifications = [.. result.Items];
                _totalPages = (int)Math.Ceiling((double)result.PaginationMetadata.TotalItemCount / PageSize);
                if (_totalPages < 1)
                {
                    _totalPages = 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.LoadError("notifications", ex), Severity.Error);
        }
        finally
        {
            IsLoading = false;
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
            await InboxService.MarkReadAsync(notification.Id, _cts.Token);

            // Update local state
            int index = _notifications.FindIndex(n => n.Id == notification.Id);
            if (index >= 0)
            {
                _notifications[index] = notification with { IsRead = true, ReadOn = DateTime.UtcNow };
            }

            // Refresh the unread count
            int count = await InboxService.GetUnreadCountAsync(_cts.Token);
            NotificationState.SetUnreadCount(count);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.SaveError("notification", ex), Severity.Error);
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
            await InboxService.MarkAllReadAsync(_cts.Token);

            // Update local state
            for (int i = 0; i < _notifications.Count; i++)
            {
                if (!_notifications[i].IsRead)
                {
                    _notifications[i] = _notifications[i] with { IsRead = true, ReadOn = DateTime.UtcNow };
                }
            }

            NotificationState.SetUnreadCount(0);
            Snackbar.Add("All notifications marked as read.", Severity.Success);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.SaveError("notifications", ex), Severity.Error);
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
