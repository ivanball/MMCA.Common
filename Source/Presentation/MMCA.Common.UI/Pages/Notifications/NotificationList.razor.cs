using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Resources;
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
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private string Title => L["Notif.List.Title"].Value;

    private List<BreadcrumbItem> _breadcrumbs = [];

    protected bool IsLoading { get; private set; }

    private IReadOnlyCollection<PushNotificationDTO> _notifications = [];

    // Localizes the wire status for display; unknown statuses fall back to the raw value (ADR-027).
    private string DisplayStatus(string status)
    {
        var localized = L[$"Notif.Status.{status}"];
        return localized.ResourceNotFound ? status : localized.Value;
    }

    protected override async Task OnInitializedAsync()
    {
        // Built here (not in a field initializer) so the injected localizer is available (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, "/", icon: Icons.Material.Filled.Home),
            new(L["Notif.List.Title"].Value, href: null, disabled: true),
        ];

        await LoadNotificationsAsync();
    }

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
            Snackbar.Add(ErrorMessages.LoadError(L["Entity.Notifications"], ex), Severity.Error);
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
