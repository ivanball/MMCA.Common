using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Auth.TwoFactor;

/// <summary>
/// The enrollment step of the two-factor page: the new secret as a QR code, as text in groups of four
/// and as an "open in app" link, then a code from the keyed app that turns the second factor on. A
/// refused code is shown inline and the panel stays, so the user can try the next code.
/// </summary>
public sealed partial class TwoFactorEnrollmentPanel : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private MudTextField<string>? _codeField;
    private string? _code;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _focused;
    private bool _disposed;

    /// <summary>Gets or sets the secret the enrollment call returned.</summary>
    [Parameter]
    [EditorRequired]
    public TwoFactorSetupResponse Setup { get; set; }

    /// <summary>Gets or sets the callback run with the recovery codes once the code verified.</summary>
    [Parameter]
    public EventCallback<TwoFactorRecoveryCodesResponse> OnConfirmed { get; set; }

    /// <summary>Gets or sets the callback run when the user abandons the enrollment.</summary>
    [Parameter]
    public EventCallback OnCancelled { get; set; }

    /// <summary>
    /// Gets or sets the callback run when the API refuses the confirmation because the session did not
    /// sign in recently; the page then offers to sign in again.
    /// </summary>
    [Parameter]
    public EventCallback OnRecentSignInRequired { get; set; }

    [Inject] private ITwoFactorUIService TwoFactorService { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <summary>Gets the shared key in groups of four, which is how authenticator apps ask for it.</summary>
    private string GroupedKey => string.Join(
        ' ',
        (Setup.SharedKey ?? string.Empty).Chunk(4).Select(chunk => new string(chunk)));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The panel replaces the "set up" button in place, so focus moves to the code field once.
        if (!_focused && _codeField is not null)
        {
            _focused = true;
            await _codeField.FocusAsync();
        }
    }

    private async Task ConfirmAsync()
    {
        _errorMessage = null;

        var code = _code?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            _errorMessage = L["Auth.Login.SecondFactor.CodeRequired"].Value;
            return;
        }

        _isBusy = true;
        try
        {
            var result = await TwoFactorService.ConfirmEnrollmentAsync(code, _cts.LifetimeToken());
            if (result.TryGetValue(out var codes))
            {
                await OnConfirmed.InvokeAsync(codes);
                return;
            }

            if (LoginRefusals.HasCode(result, LoginRefusals.RecentSignInRequiredCode))
            {
                await OnRecentSignInRequired.InvokeAsync();
                return;
            }

            _code = null;
            _errorMessage = LoginRefusals.Describe(result, L, "Auth.TwoFactor.GenericError");
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
}
