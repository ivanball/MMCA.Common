using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Auth;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// The passkeys section of a profile page (ADR-139): lists the signed-in user's passkeys, adds one
/// through the browser's WebAuthn prompt (only where <see cref="IPasskeyCapability.IsAvailable"/>; a
/// MAUI head says passkeys are added from the web app instead), renames one inline, and deletes one
/// after a confirmation. Every outcome is toasted; a refused delete (the last passkey of an account
/// with no password) shows the server's reason. Place it inside the page's own card, as with
/// <see cref="ChangePasswordCard"/>.
/// </summary>
public partial class PasskeysPanel
{
    // The code PasskeyErrors.StepUpRequired carries; UI references Shared only, so it is restated here.
    private const string StepUpRequiredCode = "Passkeys.StepUpRequired";

    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<PasskeyResponse> _passkeys = [];
    private bool _isLoading = true;
    private bool _isBusy;
    private bool _disposed;
    private string? _newName;
    private Guid? _renamingId;
    private string? _renameValue;

    /// <summary>Gets or sets the callback raised after a passkey was added, renamed or deleted.</summary>
    [Parameter]
    public EventCallback OnChanged { get; set; }

    [Inject] private IPasskeyUIService Passkeys { get; set; } = default!;

    [Inject] private IPasskeyCapability Capability { get; set; } = default!;

    [Inject] private IAppDialogService Dialogs { get; set; } = default!;

    [Inject] private IToastService Toast { get; set; } = default!;

    [Inject] private IStringLocalizer<PasskeysPanel> L { get; set; } = default!;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var result = await Passkeys.ListAsync(_cts.LifetimeToken());
            if (result.TryGetValue(out var passkeys))
            {
                _passkeys = passkeys;
            }
            else
            {
                Toast.Error(FailureMessage(result));
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task AddAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var options = await Passkeys.BeginRegistrationAsync(_cts.LifetimeToken());
            if (!options.TryGetValue(out var flow))
            {
                Toast.Error(FailureMessage(options));
                return;
            }

            var attestation = await Capability.CreateAsync(flow.Options, _cts.LifetimeToken());
            if (attestation is not { } credential)
            {
                Toast.Info(L["Passkeys.AddCancelled"].Value);
                return;
            }

            var added = await Passkeys.CompleteRegistrationAsync(
                new PasskeyRegisterRequest(flow.FlowId, credential, string.IsNullOrWhiteSpace(_newName) ? null : _newName.Trim()),
                _cts.LifetimeToken());
            await AfterChangeAsync(added, "Passkeys.Added");
            if (added.IsSuccess)
            {
                _newName = null;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void StartRename(PasskeyResponse passkey)
    {
        _renamingId = passkey.Id;
        _renameValue = passkey.Name;
    }

    private void CancelRename()
    {
        _renamingId = null;
        _renameValue = null;
    }

    private async Task SaveRenameAsync(Guid id)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var renamed = await Passkeys.RenameAsync(id, _renameValue ?? string.Empty, _cts.LifetimeToken());
            if (renamed.IsSuccess)
            {
                CancelRename();
            }

            await AfterChangeAsync(renamed, "Passkeys.Renamed");
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task DeleteAsync(PasskeyResponse passkey)
    {
        if (_isBusy)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            L["Passkeys.DeleteConfirm.Title"].Value,
            L["Passkeys.DeleteConfirm.Message", passkey.Name].Value,
            L["Passkeys.Delete"].Value,
            L["Passkeys.Cancel"].Value);
        if (!confirmed)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var deleted = await Passkeys.DeleteAsync(passkey.Id, _cts.LifetimeToken());
            await AfterChangeAsync(deleted, "Passkeys.Deleted");
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
        }
    }

    // A success is toasted and the list re-read, so the rows are the server's; a refusal shows the
    // server's own reason (the last-passkey rule, a name that is too long).
    private async Task AfterChangeAsync(Result result, string successKey)
    {
        if (result.IsFailure)
        {
            Toast.Error(FailureMessage(result));
            return;
        }

        Toast.Success(L[successKey].Value);
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    // A stale sign-in is the one refusal with a fix the user can act on, so it gets its own words (sign
    // out and back in) rather than the server's sentence.
    private string FailureMessage(Result result) =>
        result.Errors.Any(e => string.Equals(e.Code, StepUpRequiredCode, StringComparison.Ordinal))
            ? L["Passkeys.SignInAgain"].Value
            : result.LocalizedErrorMessage(L) ?? L["Passkeys.Failed"].Value;

    private string DescribeLastUsed(PasskeyResponse passkey) =>
        passkey.LastUsedOn is { } lastUsed
            ? lastUsed.ToString("d", CultureInfo.CurrentCulture)
            : L["Passkeys.NeverUsed"].Value;
}
