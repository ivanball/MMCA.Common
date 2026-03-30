using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Code-behind for the push notification compose page.
/// Collects title and body, sends to all recipients via the Notification API.
/// </summary>
public partial class NotificationSend : IDisposable
{
    [Inject] private IPushNotificationUIService NotificationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private static string Title => "Send Push Notification";

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new("Home", "/", icon: Icons.Material.Filled.Home),
        new("Push Notifications", NotificationRoutePaths.Notifications),
        new("Send", href: null, disabled: true),
    ];

    protected bool IsSaving { get; private set; }

    private string _title = string.Empty;
    private string _body = string.Empty;
    private MudForm? _form;

    private async Task SendNotificationAsync()
    {
        if (_form is null)
            return;

        await _form.ValidateAsync();
        if (!_form.IsValid)
        {
            Snackbar.Add(ErrorMessages.ValidationError, Severity.Warning);
            return;
        }

        IsSaving = true;
        try
        {
            var request = new SendPushNotificationRequest(_title, _body);
            PushNotificationDTO? result = await NotificationService.SendAsync(request, _cts.Token);

            if (result is not null)
            {
                Snackbar.Add($"Notification sent to {result.RecipientCount} recipients.", Severity.Success);
                NavigationManager.NavigateTo(NotificationRoutePaths.Notifications);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.SaveError("Notification", ex), Severity.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void NavigateToList() => NavigationManager.NavigateTo(NotificationRoutePaths.Notifications);

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
