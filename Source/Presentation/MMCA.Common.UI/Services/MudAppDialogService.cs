using MMCA.Common.UI.Common.Interfaces;
using MudBlazor;

namespace MMCA.Common.UI.Services;

/// <summary>
/// The MudBlazor-backed <see cref="IAppDialogService"/>: one of only two types in the framework
/// that name a component-library service (its sibling is <see cref="MudToastService"/>).
/// Registered by <c>AddUIShared</c>.
/// </summary>
internal sealed class MudAppDialogService(IDialogService dialogService) : IAppDialogService
{
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        // ShowMessageBoxAsync answers null when the user dismissed the dialog without choosing
        // (backdrop, escape). Collapsing that onto false is the contract: only an active
        // confirmation counts as one.
        var confirmed = await dialogService.ShowMessageBoxAsync(
            title,
            message,
            yesText: confirmText,
            cancelText: cancelText);

        return confirmed is true;
    }
}
