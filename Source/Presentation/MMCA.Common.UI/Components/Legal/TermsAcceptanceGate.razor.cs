using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Legal;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Legal;
using MudBlazor;

namespace MMCA.Common.UI.Components.Legal;

/// <summary>
/// Code-behind for the terms acceptance gate: reads the signed-in user's standing once per signed-in
/// user (re-checked when the authentication state changes to a different user), and holds the
/// non-dismissable dialog open until the user accepts the current version or signs out.
/// </summary>
/// <remarks>
/// The read runs from <see cref="OnAfterRenderAsync"/>, never during prerender, because the bearer
/// token is only readable once the renderer is interactive. Every non-success outcome of the read
/// renders nothing: the gate exists to ask for consent, never to take the app down with the API.
/// </remarks>
public partial class TermsAcceptanceGate : IDisposable
{
    /// <summary>
    /// No close button, no backdrop click, no Escape, no navigation: accepting or signing out are the
    /// only exits. The dialog provider otherwise dismisses every open dialog on LocationChanged, and a
    /// list page rewrites its own URL (page, sort, filter) right after it loads, which closed the gate
    /// for the rest of the session while consent was still owed.
    /// </summary>
    private static readonly DialogOptions DialogOptions = new()
    {
        BackdropClick = false,
        CloseOnEscapeKey = false,
        CloseOnNavigation = false,
        CloseButton = false,
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
    };

    private readonly CancellationTokenSource _disposalCts = new();

    private LegalAcceptanceDTO? _standing;
    private string? _checkedUserKey;
    private bool _visible;
    private bool _agreed;
    private bool _busy;
    private bool _subscribed;
    private string? _errorMessage;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private ILegalAcceptanceUIService LegalAcceptance { get; set; } = default!;

    [Inject]
    private IAuthUIService AuthService { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IOptions<LegalSettings> LegalOptions { get; set; } = default!;

    [Inject]
    private IStringLocalizer<SharedResource> L { get; set; } = default!;

    [Inject]
    private ILogger<TermsAcceptanceGate> Logger { get; set; } = default!;

    private LegalSettings Legal => LegalOptions.Value;

    /// <summary>"We've updated our terms" for a user who accepted an earlier version, otherwise "Please review our terms".</summary>
    private string Heading => _standing?.AcceptedVersion is null
        ? L["Legal.Gate.Title.First"]
        : L["Legal.Gate.Title.Updated"];

    /// <summary>Unsubscribes from the authentication state and cancels any read in flight.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        AuthStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _subscribed = true;

        var state = await AuthStateProvider.GetAuthenticationStateAsync();
        await EvaluateAsync(state.User);
    }

    /// <summary>Releases the subscription and the cancellation source; safe to call more than once.</summary>
    /// <param name="disposing">Whether managed state is being released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        if (_subscribed)
        {
            AuthStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
            _subscribed = false;
        }

        if (!_disposalCts.IsCancellationRequested)
        {
            _disposalCts.Cancel();
        }

        _disposalCts.Dispose();
    }

    /// <summary>
    /// The identity the cached standing belongs to: the user id claim, else the name. A change of key
    /// (sign-in, switch of account) is what triggers a fresh read.
    /// </summary>
    private static string UserKey(ClaimsPrincipal user) =>
        user.FindUserIdValue() ?? user.Identity?.Name ?? string.Empty;

    private async Task EvaluateAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            _checkedUserKey = null;
            Hide();
            StateHasChanged();
            return;
        }

        var key = UserKey(user);
        if (string.Equals(key, _checkedUserKey, StringComparison.Ordinal))
        {
            return;
        }

        _checkedUserKey = key;

        Result<LegalAcceptanceDTO> result;
        try
        {
            result = await LegalAcceptance.GetAsync(_disposalCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The user changed while the read was in flight; the newer evaluation owns the outcome.
        if (!string.Equals(key, _checkedUserKey, StringComparison.Ordinal))
        {
            return;
        }

        if (result.IsSuccess && result.Value is { CurrentVersion: not null, IsCurrent: false } standing)
        {
            _standing = standing;
            _agreed = false;
            _errorMessage = null;
            _visible = true;
        }
        else
        {
            Hide();
        }

        StateHasChanged();
    }

    private async Task AcceptAsync()
    {
        var version = _standing?.CurrentVersion;
        if (version is null || !_agreed || _busy)
        {
            return;
        }

        _busy = true;
        _errorMessage = null;
        try
        {
            var result = await LegalAcceptance.AcceptAsync(version, _disposalCts.Token);
            if (result.IsSuccess)
            {
                Hide();
                return;
            }

            // The version moved on while the dialog was open: re-read, so the user is shown (and
            // accepts) the version that is current now rather than retrying a stale one.
            if (result.Errors.Any(e => string.Equals(e.Code, LegalAcceptanceErrorCodes.VersionNotCurrent, StringComparison.Ordinal)))
            {
                _checkedUserKey = null;
                var state = await AuthStateProvider.GetAuthenticationStateAsync();
                await EvaluateAsync(state.User);
                return;
            }

            // Only a validation refusal was phrased for the user by the server. A transport fault, a
            // timeout or any server error carries a synthesized or generic message the user cannot
            // act on beyond retrying, so the gate shows its own localized wording for those.
            _errorMessage = result.HasErrorType(ErrorType.Validation)
                ? result.LocalizedErrorMessage(L) ?? L["Legal.Gate.AcceptFailed"].Value
                : L["Legal.Gate.AcceptFailed"].Value;
        }
        catch (OperationCanceledException)
        {
            // Torn down mid-request; nothing left to update.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SignOutAsync()
    {
        _busy = true;
        await AuthService.LogoutAsync();
        Navigation.NavigateTo("/login", forceLoad: true);
    }

    private void Hide()
    {
        _standing = null;
        _visible = false;
        _agreed = false;
        _errorMessage = null;
    }

    // ReevaluateAsync catches everything it can raise (see its catch blocks), so the explicit discard
    // is safe and avoids the async-void crash-the-process mode (VSTHRD100), as in BiometricGate.
    private void OnAuthenticationStateChanged(Task<AuthenticationState> task) => _ = ReevaluateAsync(task);

    private async Task ReevaluateAsync(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task;
            await InvokeAsync(() => EvaluateAsync(state.User));
        }
        catch (ObjectDisposedException)
        {
            // Renderer torn down between the auth event and the dispatch.
        }
        catch (InvalidOperationException)
        {
            // Component no longer attached; same disposition.
        }
        catch (Exception ex)
        {
            // Exhaustive by necessity (the BiometricGate precedent): the task is discarded by the
            // event handler, so nothing would observe what escapes it.
            Logger.LogError(ex, "Terms acceptance check failed after an authentication state change.");
        }
    }
}
