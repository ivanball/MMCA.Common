using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Shared actions for list pages built on <see cref="DataGridListPageBase{TDto}"/>: the
/// mobile/desktop reload dispatch and the confirm-delete-reload flow that every organizer list
/// page repeats. Kept as plain statics rather than members on the base so a page that composes
/// its own layout (or holds several grids) can reuse them without inheriting anything.
/// </summary>
public static class ListPageActions
{
    /// <summary>
    /// Reloads whichever layout is active: resets the mobile infinite-scroll list on mobile,
    /// otherwise reloads the desktop data grid's server data. Either ref may be null while the
    /// other layout is rendered.
    /// </summary>
    /// <typeparam name="TDto">The list item DTO type.</typeparam>
    /// <param name="isMobile">Whether the mobile layout is active.</param>
    /// <param name="mobileList">The mobile infinite-scroll list ref, if rendered.</param>
    /// <param name="dataGrid">The desktop data grid ref, if rendered.</param>
    public static async Task ReloadActiveLayoutAsync<TDto>(
        bool isMobile,
        MobileInfiniteScrollList<TDto>? mobileList,
        MudDataGrid<TDto>? dataGrid)
    {
        if (isMobile && mobileList is not null)
        {
            await mobileList.ResetAsync();
        }
        else if (dataGrid is not null)
        {
            await dataGrid.ReloadServerData();
        }
    }

    /// <summary>
    /// Runs the canonical destructive-action flow: confirm via <see cref="DeleteConfirmation"/>,
    /// delete, toast success, and reload the active layout; a failed <see cref="Result"/> toasts the
    /// mapped error and cancellation (disposal / InteractiveAuto render mode transition) is swallowed.
    /// </summary>
    /// <param name="deleteConfirm">The page's delete-confirmation dialog ref.</param>
    /// <param name="entityName">The display name of the entity being deleted.</param>
    /// <param name="deleteAsync">The delete call, which reports its outcome as a <see cref="Result"/>.</param>
    /// <param name="snackbar">The page's snackbar service.</param>
    /// <param name="successMessage">The localized success message.</param>
    /// <param name="errorMessage">
    /// Maps the failed result to the localized error message. Pages that show a fixed sentence
    /// ignore the argument; pages that want the API's own wording call
    /// <c>result.LocalizedErrorMessage(L)</c>.
    /// </param>
    /// <param name="reloadAsync">Reloads the page's active layout after a successful delete.</param>
    public static async Task DeleteWithConfirmationAsync(
        DeleteConfirmation deleteConfirm,
        string? entityName,
        Func<Task<Result>> deleteAsync,
        ISnackbar snackbar,
        string successMessage,
        Func<Result, string> errorMessage,
        Func<Task> reloadAsync)
    {
        ArgumentNullException.ThrowIfNull(deleteConfirm);
        ArgumentNullException.ThrowIfNull(deleteAsync);
        ArgumentNullException.ThrowIfNull(snackbar);
        ArgumentNullException.ThrowIfNull(errorMessage);
        ArgumentNullException.ThrowIfNull(reloadAsync);

        var confirmed = await deleteConfirm.ShowAsync(entityName);
        if (confirmed is not true)
        {
            return;
        }

        try
        {
            var result = await deleteAsync();
            if (result.IsFailure)
            {
                snackbar.Add(errorMessage(result), Severity.Error);
                return;
            }

            snackbar.Add(successMessage, Severity.Success);
            await reloadAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
        }
    }
}
