using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Base class for an admin create form: it owns the submit pipeline every such page otherwise
/// repeats (validate the form, run an optional pre-check, save with <see cref="CancellablePageBase.PageToken"/>,
/// clear the dirty flag before the page's own success step, keep a failed <see cref="Result"/> for the
/// error summary, and always end the saving state). The page owns its model and its service call.
/// </summary>
/// <remarks>
/// The page binds <c>@ref="Form"</c> on its <see cref="MudForm"/>, feeds <see cref="SaveResult"/> to
/// <c>&lt;ErrorSummary Result="SaveResult"&gt;</c>, reads <see cref="IsDirty"/> in its unsaved-changes
/// guard, and calls <see cref="MarkDirty"/> from each field's <c>@bind-Value:after</c>. A success
/// toast and the navigation to the created record belong in the <c>onCreated</c> callback.
/// </remarks>
public abstract class CreatePageBase : CancellablePageBase
{
    /// <summary>Gets or sets the toast service the submit pipeline reports through.</summary>
    [Inject] protected IToastService Toast { get; set; } = default!;

    /// <summary>Gets or sets the page's form. The page binds it with <c>@ref="Form"</c>.</summary>
    protected MudForm? Form { get; set; }

    /// <summary>Gets a value indicating whether a save is in flight.</summary>
    protected bool IsSaving { get; private set; }

    /// <summary>Gets a value indicating whether the reader changed a field since the page opened or last saved.</summary>
    protected bool IsDirty { get; private set; }

    /// <summary>Gets the failed result of the last save, or <see langword="null"/>. Fed to the error summary.</summary>
    protected Result? SaveResult { get; private set; }

    /// <summary>Marks the form dirty. Bound to every editable field's <c>@bind-Value:after</c>.</summary>
    protected void MarkDirty() => IsDirty = true;

    /// <summary>
    /// Validates the form, runs the optional pre-check, saves through <paramref name="saveAsync"/> with
    /// the page token and, on success, clears the dirty flag and calls <paramref name="onCreated"/>.
    /// </summary>
    /// <typeparam name="TCreated">The value the save returns on success.</typeparam>
    /// <param name="saveAsync">The page's service call; it receives the page token.</param>
    /// <param name="onCreated">The page's success step (success toast, navigation).</param>
    /// <param name="options">Optional pre-check, failure toast switch and localizer.</param>
    /// <returns>
    /// <see langword="true"/> when the save succeeded and <paramref name="onCreated"/> ran;
    /// <see langword="false"/> when the submit was ignored, stopped, cancelled or failed.
    /// </returns>
    protected async Task<bool> SubmitAsync<TCreated>(
        Func<CancellationToken, Task<Result<TCreated>>> saveAsync,
        Action<TCreated> onCreated,
        CreateSubmitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(onCreated);

        if (Form is null || IsSaving)
        {
            return false;
        }

        options ??= new CreateSubmitOptions();
        SaveResult = null;
        await Form.ValidateAsync();
        if (!Form.IsValid)
        {
            Toast.Warning(ErrorMessages.ValidationError);
            return false;
        }

        var preCheckMessage = options.PreCheck?.Invoke();
        if (preCheckMessage is not null)
        {
            Toast.Warning(preCheckMessage);
            return false;
        }

        IsSaving = true;
        try
        {
            var result = await saveAsync(PageToken);
            if (result.IsSuccess)
            {
                SaveResult = null;
                IsDirty = false;
                onCreated(result.Value!);
                return true;
            }

            SaveResult = result;
            if (options.ToastOnFailure)
            {
                result.NotifyOnFailure(Toast, options.Localizer);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
