using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Notifications;
using MMCA.Common.UI.Validation;
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
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private string Title => L["Notif.Send.Title"].Value;

    private List<BreadcrumbItem> _breadcrumbs = [];

    protected bool IsSaving { get; private set; }

    private readonly NotificationSendModel _model = new();
    private MudForm? _form;

    // One delegate for every field on the form: MudBlazor calls it with (model, member path) and the
    // model's own DataAnnotations decide the outcome, so no rule is written twice.
    private Func<object, string, IEnumerable<string>> _validate = default!;

    protected override void OnInitialized()
    {
        // Built here (not in a field initializer) so the injected localizer is available (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, "/", icon: Icons.Material.Filled.Home),
            new(L["Notif.List.Title"].Value, NotificationRoutePaths.Notifications),
            new(L["Notif.Breadcrumb.Send"].Value, href: null, disabled: true),
        ];

        // The model's ErrorMessage values are resource keys; the localizer resolves them (ADR-027).
        _validate = ModelValidation.For(_model, new DataAnnotationsModelValidator(L));
    }

    private async Task SendNotificationAsync()
    {
        if (_form is null)
            return;

        // MudForm has no OnValidSubmit, so the submit still triggers a pass; WHAT it checks comes from
        // the model's annotations, not from per-field attributes in the markup.
        await _form.ValidateAsync();
        if (!_form.IsValid)
        {
            Snackbar.Add(ErrorMessages.ValidationError, Severity.Warning);
            return;
        }

        IsSaving = true;
        try
        {
            var request = new SendPushNotificationRequest(_model.Title, _model.Body);
            PushNotificationDTO? result = await NotificationService.SendAsync(request, _cts.Token);

            if (result is not null)
            {
                Snackbar.Add(L["Notif.Send.SentTo", result.RecipientCount], Severity.Success);
                NavigationManager.NavigateTo(NotificationRoutePaths.Notifications);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
        }
        catch (Exception ex)
        {
            Snackbar.Add(ErrorMessages.SaveError(L["Entity.Notification"], ex), Severity.Error);
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
