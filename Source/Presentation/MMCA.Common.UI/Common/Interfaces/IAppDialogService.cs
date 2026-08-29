namespace MMCA.Common.UI.Common.Interfaces;

/// <summary>
/// Modal confirmation prompts, abstracted away from the component library that draws them. The one
/// shape the framework needs is a yes/no question the user must answer before something
/// irreversible (or lossy) happens, so that is all this contract exposes; richer, entity-specific
/// dialogs stay component-side (<c>DeleteConfirmation</c>).
/// <para>
/// Registered by <c>AddUIShared</c> over the MudBlazor dialog service. As with
/// <see cref="IToastService"/>, the vendor type appears in exactly one implementation, so a test
/// can answer the prompt with a stub instead of driving a rendered dialog.
/// </para>
/// </summary>
public interface IAppDialogService
{
    /// <summary>
    /// Asks the user to confirm, and resolves once they answer. Dismissing the dialog without
    /// choosing (backdrop click, escape) counts as declining, so a caller only ever has to branch
    /// on <see langword="true"/>.
    /// </summary>
    /// <param name="title">The already-localized dialog title.</param>
    /// <param name="message">The already-localized question or explanation.</param>
    /// <param name="confirmText">The already-localized label of the confirming button.</param>
    /// <param name="cancelText">The already-localized label of the declining button.</param>
    /// <returns><see langword="true"/> only when the user actively confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);
}
