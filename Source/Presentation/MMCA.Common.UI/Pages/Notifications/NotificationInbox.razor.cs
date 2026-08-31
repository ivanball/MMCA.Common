using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Code-behind for the notification inbox page.
/// Displays the current user's notifications with read/unread state and pagination.
/// Reloads the current page when a real-time push requests a refresh via
/// <see cref="NotificationState.OnRefreshRequested"/>.
/// <para>
/// The page is also the target of a typed deep link (<c>/notifications/inbox/{Id:int}</c>, rubric
/// §25): a push payload or an email can point straight at one notification. The route constraint is
/// the validation boundary, so a malformed id never reaches this component (the router renders
/// <c>NotFound</c> instead), and an id that is simply not on the loaded page degrades silently to
/// the plain inbox rather than raising an error the user can do nothing about.
/// </para>
/// </summary>
public partial class NotificationInbox : IDisposable
{
    private const int PageSize = 20;

    [Inject] private INotificationInboxUIService InboxService { get; set; } = default!;
    [Inject] private NotificationState NotificationState { get; set; } = default!;
    [Inject] private IToastService Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private IScrollManager ScrollManager { get; set; } = default!;

    /// <summary>
    /// The deep-linked notification to highlight and scroll to, from the
    /// <c>/notifications/inbox/{Id:int}</c> route. Declared as <see cref="int"/> because
    /// <c>UserNotificationIdentifierType</c> is an <see cref="int"/> alias and a route parameter's
    /// type must be written out for the <c>:int</c> constraint to bind to it. Null on the
    /// parameterless route, which renders exactly the plain inbox.
    /// </summary>
    [Parameter] public int? Id { get; set; }

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

    /// <summary>The deep-linked notification found on the loaded page, or null when there is none.</summary>
    private UserNotificationIdentifierType? _highlightedId;

    /// <summary>The id the next <see cref="OnAfterRenderAsync"/> owes a scroll, or null when none is due.</summary>
    private UserNotificationIdentifierType? _pendingScrollId;

    /// <summary>The id already scrolled to, so a re-render (or a push-driven reload) never scrolls twice.</summary>
    private UserNotificationIdentifierType? _scrolledId;

    /// <summary>The <see cref="Id"/> the deep-link state was last computed for, to detect a re-navigation.</summary>
    private UserNotificationIdentifierType? _appliedId;

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

    /// <summary>
    /// Recomputes the deep-link highlight whenever the route id changes. Navigating from
    /// <c>/notifications/inbox/5</c> to <c>/notifications/inbox/9</c> reuses this component instance,
    /// so the "already scrolled" latch is cleared here and nowhere else: without that, the second
    /// deep link would highlight but never move the viewport.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_appliedId != Id)
        {
            _appliedId = Id;
            _scrolledId = null;
        }

        ApplyDeepLinkTarget();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingScrollId is not { } id || _disposed)
        {
            return;
        }

        // Cleared before the await so a re-entrant render cannot queue the same scroll twice.
        _pendingScrollId = null;
        _scrolledId = id;

        await ScrollManager.ScrollIntoViewAsync(CardSelector(id), ScrollBehavior.Smooth);
    }

    /// <summary>
    /// Matches the route id against what the loaded page actually holds. An id that is absent (a
    /// later page, a deleted notification, or another user's) leaves the highlight off and queues no
    /// scroll: the plain inbox renders, with no toast, because a deep link the user cannot act on is
    /// not an error they caused.
    /// </summary>
    private void ApplyDeepLinkTarget()
    {
        if (Id is not { } id || id <= 0 || !_notifications.Exists(n => n.Id == id))
        {
            _highlightedId = null;
            _pendingScrollId = null;
            return;
        }

        _highlightedId = id;
        if (_scrolledId != id)
        {
            _pendingScrollId = id;
        }
    }

    private bool IsDeepLinkTarget(UserNotificationDTO notification) => _highlightedId == notification.Id;

    private static string CardElementId(UserNotificationDTO notification) => CardElementId(notification.Id);

    private static string CardElementId(UserNotificationIdentifierType id) =>
        string.Create(CultureInfo.InvariantCulture, $"notification-{id}");

    private static string CardSelector(UserNotificationIdentifierType id) => "#" + CardElementId(id);

    /// <summary>Lifts the deep-linked card off the stack; unread stays at 1 and read stays flat.</summary>
    private int CardElevation(UserNotificationDTO notification)
    {
        if (IsDeepLinkTarget(notification))
        {
            return 4;
        }

        return notification.IsRead ? 0 : 1;
    }

    private string CardClass(UserNotificationDTO notification)
    {
        var state = notification.IsRead ? "read" : "unread";
        return IsDeepLinkTarget(notification)
            ? "notification-card " + state + " deep-linked"
            : "notification-card " + state;
    }

    /// <summary>
    /// Card chrome, built from MudBlazor palette tokens only so both themes stay legible. The unread
    /// marker is a primary left border; the deep-link marker is a secondary-colored ring plus a faint
    /// surface tint, so the two read as different things when a deep-linked card is also unread.
    /// </summary>
    private string CardStyle(UserNotificationDTO notification)
    {
        var unread = notification.IsRead
            ? string.Empty
            : "border-left: 4px solid var(--mud-palette-primary);";
        return IsDeepLinkTarget(notification)
            ? unread + "box-shadow: 0 0 0 2px var(--mud-palette-secondary);background-color: var(--mud-palette-action-default-hover);"
            : unread;
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

                // Only a successful load can decide whether the deep-linked id is present, so the
                // highlight is recomputed here rather than on the failure path, which deliberately
                // leaves the previous list (and therefore the previous highlight) untouched.
                ApplyDeepLinkTarget();
            }
            else
            {
                // Same surface as the exception path it replaces: one snackbar, the list left as it
                // was rather than blanked, so a transient failure does not erase what is on screen.
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
                markRead.NotifyOnFailure(Toast, L);
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
                markAllRead.NotifyOnFailure(Toast, L);
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
            Toast.Success(L["Notif.AllMarkedRead"]);
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
