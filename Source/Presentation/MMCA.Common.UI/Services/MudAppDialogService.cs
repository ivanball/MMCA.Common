using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Forms;
using MudBlazor;

namespace MMCA.Common.UI.Services;

/// <summary>
/// The MudBlazor-backed <see cref="IAppDialogService"/>: one of only two types in the framework
/// that name a component-library service (its sibling is <see cref="MudToastService"/>).
/// Registered by <c>AddUIShared</c>. Both overloads open <see cref="ConfirmationDialog"/>, the
/// Cancel-first shape <c>DeleteConfirmation</c> uses.
/// </summary>
internal sealed class MudAppDialogService(IDialogService dialogService) : IAppDialogService
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) =>
        ConfirmAsync(title, message, confirmText, cancelText, destructive: false);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText, bool destructive) =>
        ConfirmationDialog.ShowAsync(dialogService, title, message, confirmText, cancelText, destructive);
}
