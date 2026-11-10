using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Forms;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// The confirm-delete-reload flow every admin list page repeats. A plain static rather than a
/// member on <see cref="DataGridListPageBase{TDto}"/>, so a page that composes its own layout can
/// reuse it without inheriting anything. The reload after a filter change is not here: it lives
/// on the base (<c>ReloadActiveLayoutAsync</c>), because on a phone it must save the page's
/// filters, which only the page can read.
/// </summary>
public static class ListPageActions
{
    /// <summary>
    /// Runs the canonical destructive-action flow: confirm via <see cref="DeleteConfirmation"/>,
    /// delete, toast success, and reload the active layout; a failed <see cref="Result"/> toasts the
    /// mapped error and cancellation (disposal / InteractiveAuto render mode transition) is swallowed.
    /// </summary>
    /// <param name="deleteConfirm">The page's delete-confirmation dialog ref.</param>
    /// <param name="entityName">The display name of the entity being deleted.</param>
    /// <param name="deleteAsync">The delete call, which reports its outcome as a <see cref="Result"/>.</param>
    /// <param name="toast">The toast service (see <see cref="IToastService"/>).</param>
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
        IToastService toast,
        string successMessage,
        Func<Result, string> errorMessage,
        Func<Task> reloadAsync)
    {
        ArgumentNullException.ThrowIfNull(deleteConfirm);
        ArgumentNullException.ThrowIfNull(deleteAsync);
        ArgumentNullException.ThrowIfNull(toast);
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
                toast.Error(errorMessage(result));
                return;
            }

            toast.Success(successMessage);
            await reloadAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or InteractiveAuto render mode transition
        }
    }
}
