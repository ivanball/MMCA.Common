using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
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
/// Displays a table of previously sent notifications with status and recipient count, one server
/// page at a time.
/// </summary>
public partial class NotificationList : IDisposable
{
    private const int PageSize = 20;

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
    private int _currentPage = 1;
    private int _totalPages = 1;

    /// <summary>The last load's outcome; a failure renders inline with a retry instead of the empty state.</summary>
    private Result? _loadResult;

    // Localizes the wire status for display; unknown statuses fall back to the raw value (ADR-027).
    private string DisplayStatus(string status)
    {
        var localized = L[$"Notif.Status.{status}"];
        return localized.ResourceNotFound ? status : localized.Value;
    }

    /// <summary>
    /// The phone delivery lines of one notification (ADR-044), status first and the unit explicit:
    /// "Android: delivered (1 of 1 batch)", one per platform the native leg counted. A recorded unit
    /// with no per-platform counts is a failure the sender could not attribute to a platform (FCM),
    /// shown as "Phones: failed". Empty for a row with no recorded outcome; the cell then shows "-".
    /// </summary>
    private List<PhoneDeliveryLine> PhoneDeliveryLines(PushNotificationDTO notification)
    {
        List<PhoneDeliveryLine> lines = [];
        AddPhoneDeliveryLine(lines, "Notif.Phone.Android", notification.AndroidSent, notification.AndroidFailed, notification.NativeDeliveryUnit);
        AddPhoneDeliveryLine(lines, "Notif.Phone.Apple", notification.AppleSent, notification.AppleFailed, notification.NativeDeliveryUnit);
        if (lines.Count == 0 && notification.NativeDeliveryUnit is not null)
        {
            var text = L["Notif.Phone.StatusOnly", L["Notif.Phone.AllPhones"].Value, L["Notif.Phone.Failed"].Value].Value;
            lines.Add(new PhoneDeliveryLine(text, Color.Error, Icons.Material.Filled.Error));
        }

        return lines;
    }

    private void AddPhoneDeliveryLine(List<PhoneDeliveryLine> lines, string platformKey, int? sent, int? failed, string? unit)
    {
        if (sent is null && failed is null)
        {
            return;
        }

        int delivered = sent ?? 0;
        int attempted = delivered + (failed ?? 0);
        var (statusKey, color, icon) = PhoneDeliveryStatus(delivered, attempted);

        // The unit word agrees with the attempted count: "1 of 1 batch", "2 of 3 batches". An
        // unknown unit name drops the word rather than show a raw key.
        var unitWord = L[$"Notif.Phone.Unit.{unit}.{(attempted == 1 ? "One" : "Other")}"];
        var text = unitWord.ResourceNotFound
            ? L["Notif.Phone.LineNoUnit", L[platformKey].Value, L[statusKey].Value, delivered, attempted].Value
            : L["Notif.Phone.Line", L[platformKey].Value, L[statusKey].Value, delivered, attempted, unitWord.Value].Value;
        lines.Add(new PhoneDeliveryLine(text, color, icon));
    }

    private static (string StatusKey, Color Color, string Icon) PhoneDeliveryStatus(int delivered, int attempted)
    {
        if (attempted == delivered)
        {
            return ("Notif.Phone.Delivered", Color.Success, Icons.Material.Filled.CheckCircle);
        }

        return delivered > 0
            ? ("Notif.Phone.PartlyFailed", Color.Warning, Icons.Material.Filled.Warning)
            : ("Notif.Phone.Failed", Color.Error, Icons.Material.Filled.Error);
    }

    /// <summary>One phone delivery line: the text carries the status, so color and icon are never the only cue.</summary>
    private sealed record PhoneDeliveryLine(string Text, Color Color, string Icon);

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

    /// <summary>Loads <see cref="_currentPage"/> of the history, sized by the server's own total.</summary>
    /// <returns><see langword="true"/> when the page loaded.</returns>
    private async Task<bool> LoadNotificationsAsync()
    {
        IsLoading = true;
        _loadResult = null;
        try
        {
            var result = await NotificationService.GetHistoryAsync(_currentPage, PageSize, _cts.LifetimeToken());
            _loadResult = result;
            if (result.TryGetValue(out var history))
            {
                _notifications = [.. history.Items];
                _totalPages = Math.Max(1, (int)Math.Ceiling((double)history.PaginationMetadata.TotalItemCount / PageSize));
                return true;
            }

            result.NotifyOnFailure(Toast, L);
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal
        }
        finally
        {
            IsLoading = false;
        }

        return false;
    }

    /// <summary>
    /// Moves to <paramref name="page"/>. A failed load keeps the previous page's rows on screen
    /// (L190), so the pager goes back to the page those rows belong to.
    /// </summary>
    private async Task OnPageChangedAsync(int page)
    {
        var previous = _currentPage;
        _currentPage = page;
        if (!await LoadNotificationsAsync())
        {
            _currentPage = previous;
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
