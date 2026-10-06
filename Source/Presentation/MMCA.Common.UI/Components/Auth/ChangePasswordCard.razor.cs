using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Globalization;
using MudBlazor;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// Code-behind for the shared change-password section of a profile page. Validation runs client-side
/// first (all three fields required, the new password at least <see cref="MinLength"/> characters, the
/// confirmation equal to it), each message tied to its field and listed in the form's ErrorSummary, so
/// the user sees what to fix without a server round trip. Only a valid form calls
/// <see cref="MMCA.Common.UI.Services.Auth.IAuthUIService.ChangePasswordAsync"/>; the outcome is
/// toasted with the component's own localized text and reported through <see cref="OnChanged"/> or
/// <see cref="OnFailed"/>.
/// </summary>
public partial class ChangePasswordCard
{
    private readonly CancellationTokenSource _cts = new();

    // Set by @ref after first render; the renderer owns its lifecycle, so it is not disposed here.
    private MudForm? _passwordForm;
    private string _currentPassword = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmNewPassword = string.Empty;
    private bool _isSaving;
    private bool _disposed;

    /// <summary>
    /// The minimum new-password length enforced client-side and shown in the field's helper text.
    /// Defaults to 8, the framework's password policy minimum; the server still enforces its own rules.
    /// </summary>
    [Parameter]
    public int MinLength { get; set; } = 8;

    /// <summary>Raised after the password was changed (the success toast has already been shown).</summary>
    [Parameter]
    public EventCallback OnChanged { get; set; }

    /// <summary>
    /// Raised with the failed result when the server refused the change (the failure toast has already
    /// been shown). Not raised for a client-side validation failure, which the form itself reports.
    /// </summary>
    [Parameter]
    public EventCallback<Result> OnFailed { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels any in-flight change request; safe to call more than once.</summary>
    /// <param name="disposing">Whether managed state is being released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    // Each validator returns null when valid (or empty, leaving the Required check to report a missing
    // value) or the message to show under the offending field.
    private string? ValidateNewPassword(string value) =>
        string.IsNullOrEmpty(value) || value.Length >= MinLength
            ? null
            : L.Plural("Validation.NewPasswordMinLength", MinLength, MinLength).Value;

    private string? ValidateConfirmPassword(string value) =>
        string.IsNullOrEmpty(value) || string.Equals(value, _newPassword, StringComparison.Ordinal)
            ? null
            : L["Validation.PasswordsDoNotMatch"].Value;

    private async Task SavePasswordAsync()
    {
        if (_passwordForm is not { } form)
        {
            return;
        }

        await form.ValidateAsync();
        if (!form.IsValid)
        {
            return;
        }

        _isSaving = true;
        try
        {
            var result = await AuthService.ChangePasswordAsync(_currentPassword, _newPassword, _cts.LifetimeToken());

            if (result.IsSuccess)
            {
                await form.ResetAsync();
                _currentPassword = string.Empty;
                _newPassword = string.Empty;
                _confirmNewPassword = string.Empty;
                Toast.Success(L["ChangePassword.Success"].Value);
                await OnChanged.InvokeAsync();
            }
            else
            {
                Toast.Error(L["ChangePassword.Failed"].Value);
                await OnFailed.InvokeAsync(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isSaving = false;
        }
    }
}
