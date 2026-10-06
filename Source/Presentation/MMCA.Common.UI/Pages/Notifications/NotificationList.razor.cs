using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Culture;
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
    [Inject] private IToastService Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private ViewerTimeZone ViewerTime { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    private readonly CancellationTokenSource _cts = new();

    // Whether the signed-in account holds notifications:manage, the navigation entry's own gate. Null
    // until the auth state resolves (nothing renders); false renders the 403 view and never calls the
    // history endpoint, which would refuse it anyway.
    private bool? _canManage;

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
        var user = AuthenticationState is null ? null : (await AuthenticationState).User;
        _canManage = user.HasPermissionClaim(NotificationPermissions.Manage);
        if (_canManage == false)
        {
            return;
        }

        // Built here (not in a field initializer) so the injected localizer is available (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, "/", icon: Icons.Material.Filled.Home),
            new(L["Notif.List.Title"].Value, href: null, disabled: true),
        ];

        await LoadNotificationsAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Sent times render on the viewer's clock; the zone is only readable once JS is available.
        if (firstRender && !_disposed && await ViewerTime.EnsureResolvedAsync(_cts.LifetimeToken()))
        {
            StateHasChanged();
        }
    }

    private async Task LoadNotificationsAsync()
    {
        IsLoading = true;
        try
        {
            var result = await NotificationService.GetHistoryAsync(pageNumber: 1, pageSize: 50, _cts.LifetimeToken());
            if (result.TryGetValue(out var history))
            {
                _notifications = [.. history.Items];
            }
            else
            {
                result.NotifyOnFailure(Toast, L);
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
