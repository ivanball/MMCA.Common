using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Services.Navigation;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Auth.TwoFactor;

/// <summary>
/// The signed-in account's two-factor page (<c>/profile/two-factor</c>), routable only in a host that
/// called <c>AddTwoFactorUI()</c>: the status, setting it up (QR code, text key, a code to confirm),
/// the recovery codes shown once, new recovery codes, and turning it off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolling ends this session.</b> The API does not re-issue tokens when enrollment is confirmed,
/// and the current session's token carries no second factor, so once the user has saved the recovery
/// codes the page signs out and sends them to sign in again with the password and a code.
/// </para>
/// <para>
/// An account that signs in only through an external provider or a sign-in link sets a password
/// (forgot password) or registers a passkey before enrolling: once a second factor is on, those
/// sign-in paths refuse the account.
/// </para>
/// <para>
/// The secret and the recovery codes live only in this component's memory for the state that shows
/// them; they are never logged, never put in the URL and never cached on the device.
/// </para>
/// </remarks>
public partial class TwoFactorAccount : IDisposable
{
    private const string LoginRoute = "/login";

    private readonly CancellationTokenSource _cts = new();

    private List<BreadcrumbItem> _breadcrumbs = [];
    private TwoFactorPageState _state = TwoFactorPageState.Loading;
    private TwoFactorStatusResponse _status;
    private Result? _loadResult;
    private TwoFactorSetupResponse? _setup;
    private IReadOnlyList<string> _codes = [];
    private bool _codesFromEnrollment;
    private string? _actionCode;
    private string? _actionError;
    private bool _recentSignInRequired;
    private bool _hasMultiFactor;
    private bool _isBusy;
    private bool _disposed;

    /// <summary>What the page is showing.</summary>
    private enum TwoFactorPageState
    {
        /// <summary>Reading the status.</summary>
        Loading,

        /// <summary>The status could not be read; a retry is offered.</summary>
        LoadFailed,

        /// <summary>The second factor is off; setting it up is offered.</summary>
        NotEnrolled,

        /// <summary>The new secret is shown and a code is asked for.</summary>
        Enrolling,

        /// <summary>A new set of recovery codes is shown, once.</summary>
        RecoveryCodes,

        /// <summary>The second factor is on.</summary>
        Enabled,
    }

    /// <summary>
    /// Gets or sets a value indicating whether the visitor was sent here because the API refused the
    /// session for want of a second factor; the page then says the account's role requires one.
    /// </summary>
    [SupplyParameterFromQuery(Name = "required")]
    public bool Required { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    [Inject] private ITwoFactorUIService TwoFactorService { get; set; } = default!;

    [Inject] private IAuthUIService AuthService { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private IAppDialogService Dialogs { get; set; } = default!;

    [Inject] private IToastService Toast { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels any in-flight call; safe to call more than once.</summary>
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

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        // Built here (not in a field initializer) so the injected localizer is available (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, RoutePaths.Home, icon: Icons.Material.Filled.Home),
            new(L["Auth.TwoFactor.Title"].Value, href: null, disabled: true),
        ];

        if (AuthState is not null)
        {
            _hasMultiFactor = (await AuthState).User.HasMultiFactor();
        }

        await LoadStatusAsync();
    }

    private async Task LoadStatusAsync()
    {
        _state = TwoFactorPageState.Loading;
        _loadResult = null;

        try
        {
            var result = await TwoFactorService.GetStatusAsync(_cts.LifetimeToken());
            _loadResult = result;
            if (result.TryGetValue(out var status))
            {
                _status = status;
                _state = status.IsEnabled ? TwoFactorPageState.Enabled : TwoFactorPageState.NotEnrolled;
            }
            else
            {
                _state = TwoFactorPageState.LoadFailed;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal.
        }
    }

    private async Task BeginAsync()
    {
        if (!TryStartAction())
        {
            return;
        }

        try
        {
            var result = await TwoFactorService.BeginEnrollmentAsync(_cts.LifetimeToken());
            if (result.TryGetValue(out var setup))
            {
                _setup = setup;
                _state = TwoFactorPageState.Enrolling;
                return;
            }

            ShowRefusal(result);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void OnEnrollmentConfirmed(TwoFactorRecoveryCodesResponse response)
    {
        // The secret has served its purpose: drop it before the codes are shown.
        _setup = null;
        _codes = response.RecoveryCodes;
        _codesFromEnrollment = true;
        _state = TwoFactorPageState.RecoveryCodes;
    }

    private void OnEnrollmentCancelled()
    {
        _setup = null;
        _state = TwoFactorPageState.NotEnrolled;
    }

    private void OnRecentSignInRequired()
    {
        _setup = null;
        _recentSignInRequired = true;
        _state = TwoFactorPageState.NotEnrolled;
    }

    /// <summary>
    /// The user saved the codes. After an enrollment the session (whose token has no second factor) is
    /// ended and the user signs in again with a code; after a regeneration the status is read again.
    /// </summary>
    private async Task OnCodesSavedAsync()
    {
        _codes = [];
        if (_codesFromEnrollment)
        {
            await SignOutAndSignInAgainAsync(LoginRefusals.TwoFactorEnabledReason);
            return;
        }

        await LoadStatusAsync();
    }

    private async Task RegenerateAsync()
    {
        if (ActionCode() is not { } code || !TryStartAction())
        {
            return;
        }

        try
        {
            var result = await TwoFactorService.RegenerateRecoveryCodesAsync(code, _cts.LifetimeToken());
            _actionCode = null;
            if (result.TryGetValue(out var response))
            {
                _codes = response.RecoveryCodes;
                _codesFromEnrollment = false;
                _state = TwoFactorPageState.RecoveryCodes;
                return;
            }

            ShowRefusal(result);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task DisableAsync()
    {
        if (ActionCode() is not { } code || !TryStartAction())
        {
            return;
        }

        try
        {
            var confirmed = await Dialogs.ConfirmAsync(
                L["Auth.TwoFactor.Disable.ConfirmTitle"],
                L["Auth.TwoFactor.Disable.ConfirmMessage"],
                L["Auth.TwoFactor.Disable"],
                L["Common.Button.Cancel"],
                destructive: true);
            if (!confirmed)
            {
                return;
            }

            var result = await TwoFactorService.DisableAsync(code, _cts.LifetimeToken());
            _actionCode = null;
            if (result.IsSuccess)
            {
                Toast.Success(L["Auth.TwoFactor.Disabled"].Value);
                _status = default;
                _state = TwoFactorPageState.NotEnrolled;
                return;
            }

            ShowRefusal(result);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private Task SignInAgainAsync() => SignOutAndSignInAgainAsync(LoginRefusals.TwoFactorRequiredReason);

    /// <summary>Ends this session and opens the sign-in page with a notice and the return address.</summary>
    /// <param name="reason">The notice the sign-in page shows.</param>
    private async Task SignOutAndSignInAgainAsync(string reason)
    {
        await AuthService.LogoutAsync();

        var target = $"{LoginRoute}?reason={Uri.EscapeDataString(reason)}";
        var returnUrl = ReturnUrlProtector.Sanitize(ReturnUrl, "/");
        if (!string.Equals(returnUrl, "/", StringComparison.Ordinal))
        {
            target += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        Navigation.NavigateTo(target, forceLoad: true);
    }

    /// <summary>Clears the previous refusal and claims the busy flag; false when an action is already running.</summary>
    /// <returns><see langword="true"/> when the action may start.</returns>
    private bool TryStartAction()
    {
        if (_isBusy)
        {
            return false;
        }

        _actionError = null;
        _recentSignInRequired = false;
        _isBusy = true;
        return true;
    }

    /// <summary>The trimmed code for a code-proved action, or null (with the prompt shown) when it is empty.</summary>
    /// <returns>The code, or <see langword="null"/>.</returns>
    private string? ActionCode()
    {
        var code = _actionCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            _actionError = L["Auth.Login.SecondFactor.CodeRequired"].Value;
            return null;
        }

        return code;
    }

    /// <summary>Shows a refused action: the sign-in-again notice for a stale session, otherwise its words.</summary>
    /// <param name="result">The failed result.</param>
    private void ShowRefusal(Result result)
    {
        if (LoginRefusals.HasCode(result, LoginRefusals.RecentSignInRequiredCode))
        {
            _recentSignInRequired = true;
            return;
        }

        _actionError = LoginRefusals.Describe(result, L, "Auth.TwoFactor.GenericError");
    }
}
