using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
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
    [Inject] private IToastService Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private INotificationScopeProvider ScopeProvider { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private string Title => L["Notif.Send.Title"].Value;

    private List<BreadcrumbItem> _breadcrumbs = [];

    protected bool IsSaving { get; private set; }

    private readonly NotificationSendModel _model = new();
    private MudForm? _form;

    /// <summary>
    /// The last send attempt's outcome, rendered inline by the shared <c>ErrorSummary</c>. Null
    /// before the first attempt and cleared at the start of every new one.
    /// </summary>
    private Result? _sendResult;

    // One delegate for every field on the form: MudBlazor calls it with (model, member path) and the
    // model's own DataAnnotations decide the outcome, so no rule is written twice.
    private Func<object, string, IEnumerable<string>> _validate = default!;

    /// <summary>
    /// The caption naming the scope this send will be auto-targeted at, already localized. Null when
    /// the application runs unscoped or the provider has no display name, in which case the page
    /// renders no caption at all rather than an empty line.
    /// </summary>
    private string? _scopeCaption;

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

    protected override async Task OnInitializedAsync()
    {
        // A scoped application applies its scope to the send automatically, so without a caption the
        // operator is composing a broadcast with no visible statement of who receives it. The
        // localized string is built here rather than in a field initializer because it needs the
        // injected localizer (ADR-027).
        try
        {
            var scopeName = await ScopeProvider.GetCurrentScopeDisplayNameAsync(_cts.Token);
            if (!string.IsNullOrWhiteSpace(scopeName))
            {
                _scopeCaption = L["Notif.Send.Targeting", scopeName].Value;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
        }
    }

    private async Task SendNotificationAsync()
    {
        if (_form is null)
            return;

        _sendResult = null;

        // MudForm has no OnValidSubmit, so the submit still triggers a pass; WHAT it checks comes from
        // the model's annotations, not from per-field attributes in the markup.
        await _form.ValidateAsync();
        if (!_form.IsValid)
        {
            // The per-field messages are already on screen; the ErrorSummary above the form collects
            // them in one place (deduplicated) and the snackbar stays the summary cue it always was.
            Toast.Warning(ErrorMessages.ValidationError);
            return;
        }

        IsSaving = true;
        try
        {
            var request = new SendPushNotificationRequest(_model.Title, _model.Body);
            var result = await NotificationService.SendAsync(request, _cts.Token);
            _sendResult = result;

            if (result.TryGetValue(out PushNotificationDTO? sent))
            {
                Toast.Success(L["Notif.Send.SentTo", sent.RecipientCount]);
                NavigationManager.NavigateTo(NotificationRoutePaths.Notifications);
            }
            else
            {
                // Rendered inline by the ErrorSummary as well, so the wording survives the snackbar
                // timing out on a long form.
                result.NotifyOnFailure(Toast, L);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
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
