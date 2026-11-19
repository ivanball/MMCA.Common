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
/// partial class. The precondition read never throws: a failure before the owner opted in keeps the
/// gate open, and a failure after it fails closed (see <see cref="ShouldLockAsync"/>).
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
    private ElementReference _lockRoot;
    private AppLockInertBackground? _inertBackground;

    /// <summary>
    /// Set when the panel has just become the only thing the user can act on: on every lock (so
    /// focus is on Unlock while the platform prompt is still open, not left on the page behind) and
    /// again after a declined or unavailable prompt. Acted on from <see cref="OnAfterRenderAsync"/>
    /// because the button reference only exists once the locked branch has actually rendered.
    /// </summary>
    private bool _focusUnlockPending;

    /// <summary>
    /// Set on every lock; acted on from <see cref="OnAfterRenderAsync"/>, once the overlay wrapper the
    /// inert boundary is measured from has rendered.
    /// </summary>
    private bool _inertPending;

    private AppLockInertBackground InertBackground => _inertBackground ??= new AppLockInertBackground(JS);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusUnlockPending && _unlockButton is not null)
        {
            _focusUnlockPending = false;
            await FocusUnlockAsync();
        }

        if (_inertPending && _locked)
        {
            _inertPending = false;
            await InertBackground.ApplyAsync(_lockRoot);

            // An unlock that landed while the call was in flight found nothing applied to release.
            if (!_locked)
            {
                await InertBackground.ReleaseAsync();
            }
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

        EngageLock();
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

        // Fire and forget: IDisposable cannot await, and the helper never throws (each call is best
        // effort). A gate torn down while locked must not leave the page behind it inert.
        if (_inertBackground is { } inertBackground)
        {
            _inertBackground = null;
            _ = inertBackground.DisposeAsync().AsTask();
        }
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

                EngageLock();
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
#pragma warning disable CA1031 // Discarded event-handler task: nothing would observe what escapes it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Exhaustive by necessity: OnResumed discards this task, so nothing would observe what
            // escapes it and a failed re-lock would go unreported.
            Logger.LogError(ex, "Re-locking the app after resume failed.");
        }
    }

    /// <summary>
    /// Whether the gate must lock now. Never throws (it runs from the first render and from resume):
    /// an unreadable preference means nothing says the owner opted in, so the gate stays open rather
    /// than forcing a biometric prompt on a device that may have none enrolled; an unreadable stored
    /// session AFTER the owner opted in fails closed, because the session state is unknown.
    /// </summary>
    private async Task<bool> ShouldLockAsync()
    {
        bool appLockEnabled;
        try
        {
            appLockEnabled = await Preferences.GetAsync(DevicePreferenceKeys.AppLockEnabled, false);
        }
#pragma warning disable CA1031 // A failed preference read leaves the gate open (logged)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogWarning(ex, "Reading the app-lock preference failed; the gate stays open.");
            return false;
        }

        if (!appLockEnabled)
        {
            return false;
        }

        try
        {
            // Nothing to protect without a stored session.
            return await TokenStorage.GetRefreshTokenAsync() is not null;
        }
#pragma warning disable CA1031 // A failed session read after app lock was enabled locks the gate (logged)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogWarning(ex, "Reading the stored session failed after app lock was enabled; the gate locks.");
            return true;
        }
    }

    /// <summary>
    /// Engages the gate: every lock queues focus onto Unlock and the inert background, whichever
    /// path locked it (start-up or resume), so focus never stays on the page behind the overlay.
    /// </summary>
    private void EngageLock()
    {
        _locked = true;
        _focusUnlockPending = true;
        _inertPending = true;
    }

    private async Task UnlockAsync()
    {
        if (await Biometrics.AuthenticateAsync(L["Prompt.Reason"].Value))
        {
            _locked = false;
            _focusUnlockPending = false;
            _inertPending = false;
            await ReleaseBackgroundAsync();
#pragma warning disable BL0012 // UnlockAsync also runs from OnAfterRenderAsync and ReLockAsync, where nothing re-renders automatically
            StateHasChanged();
#pragma warning restore BL0012
            return;
        }

        // Still locked: the panel is now the whole interaction surface, so its primary action has to
        // hold focus. Queued rather than applied here, because the first lock runs before the locked
        // branch has rendered and the button reference is still null.
        _focusUnlockPending = true;
#pragma warning disable BL0012 // UnlockAsync also runs from OnAfterRenderAsync and ReLockAsync, where nothing re-renders automatically
        StateHasChanged();
#pragma warning restore BL0012
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

    /// <summary>
    /// The app's own logout, never a bare token clear: clearing the tokens alone left Blazor's auth
    /// state signed in and the previous session's read cache and offline snapshots on the device.
    /// </summary>
    private async Task SignOutAsync()
    {
        await AuthService.LogoutAsync();
        _locked = false;
        _focusUnlockPending = false;
        _inertPending = false;
        await ReleaseBackgroundAsync();
        Navigation.NavigateTo("/login");
    }

    /// <summary>Lifts the inert background, when a lock applied one.</summary>
    private Task ReleaseBackgroundAsync() =>
        _inertBackground is { } inertBackground ? inertBackground.ReleaseAsync() : Task.CompletedTask;
}
