using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Auth;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// The sign-in page's passkey button and passkey autofill (ADR-139). A click asks the server for a
/// challenge, lets the browser prompt for a passkey, and posts the assertion back; on success the
/// token pair is already stored (through <see cref="IPasskeyUIService.SignInAsync"/>) and
/// <see cref="OnSignedIn"/> runs so the page can land the user.
/// <para>
/// <b>Autofill.</b> After the first render, and only where the browser supports conditional mediation
/// (<see cref="IPasskeyCapability.IsAutofillAvailableAsync"/>, asked before any options are requested,
/// so an unsupported browser stores no challenge), it also starts autofill, so the browser lists the user's passkeys among the email field's suggestions (the page marks that
/// field <c>autocomplete="email webauthn"</c>). Picking one completes the same way a click does.
/// </para>
/// </summary>
public partial class PasskeySignInButton : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string? _errorMessage;
    private bool _isBusy;
    private bool _disposed;

    /// <summary>Gets or sets the callback raised once the passkey signed the user in and the tokens are stored.</summary>
    [Parameter]
    public EventCallback OnSignedIn { get; set; }

    /// <summary>Gets or sets a value indicating whether passkey autofill is started after the first render. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool EnableAutofill { get; set; } = true;

    [Inject] private IPasskeyUIService Passkeys { get; set; } = default!;

    [Inject] private IPasskeyCapability Capability { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

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
        await Capability.CancelAutofillAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !EnableAutofill || _disposed)
        {
            return;
        }

        try
        {
            // Support first: options store a server challenge, which a browser without conditional
            // mediation could never redeem.
            if (!await Capability.IsAutofillAvailableAsync(_cts.LifetimeToken()))
            {
                return;
            }

            var options = await Passkeys.BeginSignInAsync(_cts.LifetimeToken());
            if (options.TryGetValue(out var flow))
            {
                await Capability.StartAutofillAsync(
                    flow.Options,
                    credential => InvokeAsync(() => CompleteAsync(flow.FlowId, credential)),
                    _cts.LifetimeToken());
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
    }

    private async Task SignInAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _errorMessage = null;
        _isBusy = true;
        try
        {
            await Capability.CancelAutofillAsync();
            var options = await Passkeys.BeginSignInAsync(_cts.LifetimeToken());
            if (!options.TryGetValue(out var flow))
            {
                _errorMessage = FailureMessage(options);
                return;
            }

            var credential = await Capability.GetAsync(flow.Options, _cts.LifetimeToken());
            if (credential is not { } assertion)
            {
                _errorMessage = L["Auth.Login.PasskeyCancelled"].Value;
                return;
            }

            await CompleteAsync(flow.FlowId, assertion);
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

    private async Task CompleteAsync(string flowId, JsonElement assertion)
    {
        var result = await Passkeys.SignInAsync(new PasskeySignInRequest(flowId, assertion), _cts.LifetimeToken());
        if (result.IsFailure)
        {
            _errorMessage = FailureMessage(result);
            StateHasChanged();
            return;
        }

        await OnSignedIn.InvokeAsync();
    }

    private string FailureMessage(Result result) =>
        result.LocalizedErrorMessage(L) ?? L["Auth.Login.PasskeyFailed"].Value;
}
