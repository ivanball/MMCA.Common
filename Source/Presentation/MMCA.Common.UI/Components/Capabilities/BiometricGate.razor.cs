using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MudBlazor;

namespace MMCA.Common.UI.Components.Capabilities;

/// <summary>
/// Code-behind for the app-lock overlay (ADR-042 Wave 4): the re-arm-on-resume lifecycle, the
/// preference/stored-token precondition, and the focus handling that makes the locked panel a real
/// interaction boundary rather than a picture of one. The markup half is
/// <c>BiometricGate.razor</c>, which supplies the injected services as properties on this same
/// partial class.
/// </summary>
public partial class BiometricGate
{
    /// <summary>
    /// How long the app may sit in the background before the gate re-arms. Short by design: a
    /// device handed to someone else is usually away from the owner for longer than a glance at a
    /// notification, and re-prompting costs one biometric touch.
    /// </summary>
    [Parameter]
    public TimeSpan ReLockAfter { get; set; } = TimeSpan.FromSeconds(30);

    private bool _locked;
    private bool _subscribed;
    private MudButton? _unlockButton;

    /// <summary>
    /// Set when the panel has just become the only thing the user can act on (the platform prompt
    /// was declined or unavailable). Acted on from <see cref="OnAfterRenderAsync"/> because the
    /// button reference only exists once the locked branch has actually rendered.
    /// </summary>
    private bool _focusUnlockPending;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusUnlockPending && _unlockButton is not null)
        {
            _focusUnlockPending = false;
            await FocusUnlockAsync();
        }

        if (!firstRender)
        {
            return;
        }

        // Subscribed regardless of the current preference: app lock can be switched on later in the
        // same session, and the handler re-reads the preference every time it fires.
        AppLifecycle.Resumed += OnResumed;
        _subscribed = true;

        if (!await ShouldLockAsync())
        {
            return;
        }

        _locked = true;
        StateHasChanged();
        await UnlockAsync();
    }

    /// <summary>Unsubscribes from the lifecycle notifier; safe to call more than once.</summary>
    /// <param name="disposing">Whether managed state is being released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !_subscribed)
        {
            return;
        }

        AppLifecycle.Resumed -= OnResumed;
        _subscribed = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // SECURITY: the render tree survives a background/foreground cycle on a hybrid head, so without
    // this the gate would engage once at app start and never again: anyone handed the unlocked
    // device reopens the app straight into the signed-in session.
    //
    // Event-handler signature; the re-lock task observes its own failures internally (catch-all), so
    // the explicit discard is safe and avoids the async-void crash-the-process mode (VSTHRD100).
    private void OnResumed(object? sender, AppResumedEventArgs args) => _ = ReLockAsync(args);

    private async Task ReLockAsync(AppResumedEventArgs args)
    {
        try
        {
            if (_locked || args.BackgroundDuration < ReLockAfter)
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                if (!await ShouldLockAsync())
                {
                    return;
                }

                _locked = true;
                StateHasChanged();
                await UnlockAsync();
            });
        }
        catch (ObjectDisposedException)
        {
            // Renderer torn down between the native event and the dispatch.
        }
        catch (InvalidOperationException)
        {
            // Component no longer attached; same disposition.
        }
        catch (Exception ex)
        {
            // Exhaustive by necessity: this is an async void event handler, so anything escaping
            // here has no caller to observe it, and on a native head that terminates the process.
            Logger.LogError(ex, "Re-locking the app after resume failed.");
        }
    }

    private async Task<bool> ShouldLockAsync()
    {
        var appLockEnabled = await Preferences.GetAsync(DevicePreferenceKeys.AppLockEnabled, false);
        if (!appLockEnabled)
        {
            return false;
        }

        // Nothing to protect without a stored session.
        return await TokenStorage.GetRefreshTokenAsync() is not null;
    }

    private async Task UnlockAsync()
    {
        if (await Biometrics.AuthenticateAsync(L["Prompt.Reason"].Value))
        {
            _locked = false;
            StateHasChanged();
            return;
        }

        // Still locked: the panel is now the whole interaction surface, so its primary action has to
        // hold focus. Queued rather than applied here, because the first lock runs before the locked
        // branch has rendered and the button reference is still null.
        _focusUnlockPending = true;
        StateHasChanged();
    }

    /// <summary>
    /// Moves focus to the Unlock button, tolerating the two ways a focus call legitimately cannot
    /// land: a prerender pass with no browser attached, and a circuit or renderer already torn down.
    /// </summary>
    private async Task FocusUnlockAsync()
    {
        try
        {
            await _unlockButton!.FocusAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to focus.
        }
        catch (InvalidOperationException)
        {
            // Prerendering (no JS runtime) or the element was removed between render and focus.
        }
    }

    private async Task SignOutAsync()
    {
        await TokenStorage.ClearTokensAsync();
        _locked = false;
        Navigation.NavigateTo("/login");
    }
}
