using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Code-behind for the push notification history page.
/// Displays a table of previously sent notifications with status and recipient count.
/// </summary>
public partial class NotificationList : IDisposable
{
    [Inject] private IPushNotificationUIService NotificationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private static string Title => "Push Notifications";

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new("Home", "/", icon: Icons.Material.Filled.Home),
        new("Push Notifications", href: null, disabled: true),
    ];

    protected bool IsLoading { get; private set; }

    private IReadOnlyCollection<PushNotificationDTO> _notifications = [];

    protected override async Task OnInitializedAsync() => await LoadNotificationsAsync();

    private async Task LoadNotificationsAsync()
    {
        IsLoading = true;
        try
        {
            var result = await NotificationService.GetHistoryAsync(pageNumber: 1, pageSize: 50, _cts.Token);
            _notifications = result?.Items is not null ? [.. result.Items] : [];
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

    private void NavigateToSend() => NavigationManager.NavigateTo(NotificationRoutePaths.NotificationSend);

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
