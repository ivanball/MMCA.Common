using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// A reusable "did not get the email?" block: one button that asks for a fresh confirmation link for
/// <see cref="Email"/> through <see cref="IEmailConfirmationUIService"/>. The shared sign-in page shows
/// it when sign-in is refused because the address is unconfirmed, and the register page shows it in its
/// "check your email" state.
/// </summary>
/// <remarks>
/// The endpoint answers 202 for every well-formed address, so the success notice never claims that an
/// account exists. The button is disabled after one successful send for the rest of the prompt's life;
/// a refusal (a rate limit arrives localized) is shown and the button stays available. An in-flight
/// request is cancelled when the prompt is disposed.
/// </remarks>
public sealed partial class EmailConfirmationResendPrompt : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private bool _isSending;
    private bool _sent;
    private string? _errorMessage;
    private bool _disposed;

    /// <summary>Gets or sets the address the link is sent to.</summary>
    [Parameter]
    [EditorRequired]
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the callback run after the request was accepted.</summary>
    [Parameter]
    public EventCallback OnSent { get; set; }

    [Inject] private IEmailConfirmationUIService ConfirmationService { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

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

    private async Task ResendAsync()
    {
        if (_isSending || _sent)
        {
            return;
        }

        _errorMessage = null;
        var email = Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            _errorMessage = L["Auth.Confirm.EmailRequired"].Value;
            return;
        }

        _isSending = true;
        try
        {
            var result = await ConfirmationService.ResendEmailConfirmationAsync(email, _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                _sent = true;
                await OnSent.InvokeAsync();
                return;
            }

            _errorMessage = result.LocalizedErrorMessage(L) ?? L["Auth.Confirm.GenericError"].Value;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isSending = false;
        }
    }
}
