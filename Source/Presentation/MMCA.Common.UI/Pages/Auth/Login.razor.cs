using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the <c>/login</c> page: the external sign-in refusal it is sent back with, the
/// words for a refused password sign-in, and the page lifetime that cancels an in-flight sign-in.
/// </summary>
public partial class Login : IDisposable
{
    // Cancelled on dispose, so a sign-in still in flight when the user navigates away (or an
    // InteractiveAuto render-mode transition tears the page down) stops instead of completing into
    // a component that is gone.
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Set when device storage dropped the OAuth attempt; the web-redirect provider buttons are then
    /// disabled, because a flow started there would be refused at completion.
    /// </summary>
    private bool _externalSignInUnavailable;

    /// <summary>
    /// The address registration refused because it already holds an account, when the visitor was
    /// sent here from there; drives the existing-account notice.
    /// </summary>
    private string? _existingAccountEmail;

    /// <summary>The address the last sign-in link request went to, for the sent notice.</summary>
    private string? _signInLinkSentTo;

    private bool _isSendingLink;

    /// <summary>The configured OIDC providers, one sign-in button each.</summary>
    private IReadOnlyList<OidcProviderOption> _oidcProviders = [];

    /// <summary>Whether any OIDC provider declares email domains, which offers the organization step.</summary>
    private bool _offersOrganizationSignIn;

    private bool _isDiscovering;

    /// <summary>Whether the page offers passkey sign-in and autofill (host opt-in AND a WebAuthn-capable head).</summary>
    private bool _offersPasskeys;

    /// <summary>
    /// Gets or sets the error code of a refused external sign-in. The OAuth completion endpoint sends
    /// every refusal back here as <c>/login?error={code}</c>: <c>oauth_failed</c>,
    /// <c>missing_claims</c>, or the first domain error code (for example <c>User.LastName.Empty</c>).
    /// </summary>
    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels any in-flight sign-in; safe to call more than once.</summary>
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

    /// <summary>
    /// Prefills the address another auth page handed over in history state (never the URL). An
    /// address registration refused as already registered also raises the existing-account notice.
    /// </summary>
    private void ApplyHandedOverEmail()
    {
        var email = AuthEmailPrefill.Read(Navigation, out var existingAccount);
        if (email is null)
        {
            return;
        }

        _model.Email = email;
        if (existingAccount)
        {
            _existingAccountEmail = email;
        }
    }

    /// <summary>
    /// Whether to offer passkeys (ADR-139): the host turned it on (<c>SignIn:OfferPasskeys</c>) and the
    /// head's <see cref="IPasskeyCapability"/> can run WebAuthn. The capability is resolved optionally,
    /// so a host (or a component test) that registers none simply gets no button.
    /// </summary>
    private bool OffersPasskeys() =>
        SignInOptions.Value.OfferPasskeys
        && Services.GetService<IPasskeyCapability>() is { IsAvailable: true }
        && Services.GetService<IPasskeyUIService>() is not null;

    /// <summary>A passkey signed the user in (the tokens are stored): land them as a password sign-in does.</summary>
    private Task OnPasskeySignedInAsync() =>
        ApplyStoredPreferencesAndNavigateAsync(ReturnUrlProtector.Sanitize(ReturnUrl, "/"));

    /// <summary>Opens Forgot password with the typed address already filled in.</summary>
    private void OpenPasswordReset() =>
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/forgot-password", _model.Email ?? string.Empty);

    /// <summary>
    /// Asks for a sign-in link to the typed address (ADR-116). The endpoint answers 202 for every
    /// well-formed address, so the notice says "if an account exists" and nothing more.
    /// </summary>
    private async Task SendSignInLinkAsync()
    {
        _errorMessage = null;
        _signInLinkSentTo = null;

        var email = _model.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            _errorMessage = L["Auth.Login.EmailMeLink.EnterEmail"].Value;
            return;
        }

        // Resolved at the click rather than injected, so a host (or a component test) that never offers
        // the button does not have to register the client at all.
        if (Services.GetService<ISignInLinkUIService>() is not { } signInLinks)
        {
            _errorMessage = L["Auth.Login.GenericError"].Value;
            return;
        }

        _isSendingLink = true;
        try
        {
            var result = await signInLinks.RequestSignInLinkAsync(email, _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                _signInLinkSentTo = email;
                return;
            }

            _errorMessage = result.LocalizedErrorMessage(L) ?? L["Auth.Login.GenericError"].Value;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isSendingLink = false;
        }
    }

    /// <summary>
    /// Starts a configured OIDC provider's sign-in the way the built-in providers start theirs: through
    /// the system-browser broker on a native head, otherwise through the web redirect.
    /// </summary>
    /// <param name="provider">The provider name, its <c>/auth/oauth/{name}</c> route segment.</param>
    private Task SignInWithOidcAsync(string provider) =>
        ExternalAuthBroker.IsAvailable ? SignInWithBrokerAsync(provider) : SignInWithRedirectAsync(provider);

    /// <summary>
    /// The email-first "Sign in with your organization" step: asks the API which configured OIDC
    /// provider claims the typed address's domain and starts that provider's sign-in. An address no
    /// provider claims stays on the page with an explanation.
    /// </summary>
    private async Task SignInWithOrganizationAsync()
    {
        _errorMessage = null;

        var email = _model.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            _errorMessage = L["Auth.Login.SignInWithOrganization.EnterEmail"].Value;
            return;
        }

        // Resolved at the click, like the sign-in-link client, so a host or a component test that
        // never offers the step does not have to register it.
        if (Services.GetService<IOidcDiscoveryUIService>() is not { } discovery)
        {
            _errorMessage = L["Auth.Login.GenericError"].Value;
            return;
        }

        _isDiscovering = true;
        try
        {
            var match = await discovery.DiscoverAsync(email, _cts.LifetimeToken());
            if (match is null)
            {
                _errorMessage = L["Auth.Login.SignInWithOrganization.NotFound"].Value;
                return;
            }

            await SignInWithOidcAsync(match.Provider);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        catch (HttpRequestException)
        {
            _errorMessage = L["Auth.Login.GenericError"].Value;
        }
        finally
        {
            _isDiscovering = false;
        }
    }

    /// <summary>
    /// Maps the machine error code to words; the raw code is never shown. Any code other than the
    /// two transport-level ones is a domain refusal of the account. A refusal whose code carries a
    /// recovery path of its own (a locked account, an email already linked to another sign-in, an
    /// unverified provider email) has a resource <c>Auth.Login.ExternalError.{code}</c>; every other
    /// code (for example an invalid name) falls back to the generic refusal.
    /// </summary>
    /// <param name="errorCode">The code from the query string, or null when there is none.</param>
    /// <returns>The localized message, or null when nothing failed.</returns>
    private string? ExternalSignInErrorMessage(string? errorCode) => errorCode switch
    {
        null or "" => null,
        "oauth_failed" => L["Auth.Login.ExternalError.Failed"].Value,
        "missing_claims" => L["Auth.Login.ExternalError.MissingClaims"].Value,
        _ => RefusalMessage(errorCode),
    };

    private string RefusalMessage(string errorCode)
    {
        var specific = L["Auth.Login.ExternalError." + errorCode];
        return specific.ResourceNotFound ? L["Auth.Login.ExternalError.Refused"].Value : specific.Value;
    }

    /// <summary>
    /// Words for a refused password sign-in. The API's message is an English sentence, not a resource
    /// key, so a refusal whose error code has a resource of its own (<c>Auth.InvalidCredentials</c>,
    /// <c>Auth.AccountLocked</c>) is shown in the active culture by that code. Any other refusal keeps
    /// the previous chain: the API's message, localized when it can be, then the generic
    /// invalid-credentials wording when the failure carried nothing renderable.
    /// </summary>
    /// <param name="result">The failed sign-in result.</param>
    /// <returns>The message for the alert.</returns>
    private string PasswordLoginErrorMessage(Result result)
    {
        if (result.Errors is [var error] && !string.IsNullOrEmpty(error.Code))
        {
            var byCode = L[error.Code];
            if (!byCode.ResourceNotFound)
            {
                return byCode.Value;
            }
        }

        return result.LocalizedErrorMessage(L) ?? L["Auth.Login.InvalidCredentials"].Value;
    }

    /// <summary>
    /// Starts a web-redirect provider sign-in: writes the per-attempt OAuth state the completion page
    /// requires back, then leaves for the provider with it. The attempt is minted at the click (M237),
    /// so its lifetime runs from the moment the user chose a provider, and a second login tab only
    /// replaces the attempt when the user clicks there (the latest click wins). When device storage
    /// accepts the attempt without keeping it (storage full, site data blocked, private mode) the
    /// completion would refuse the flow one redirect later with no explanation, so this says so now,
    /// stays on the page and disables the web-redirect providers.
    /// </summary>
    /// <param name="provider">The provider segment of the challenge URL.</param>
    private async Task SignInWithRedirectAsync(string provider)
    {
        string? oauthState;
        try
        {
            oauthState = await OAuthFlowState.BeginAsync(_cts.LifetimeToken());
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // The page went away mid-write: there is nothing left to navigate from.
            return;
        }
        catch (InvalidOperationException)
        {
            _externalSignInUnavailable = true;
            _errorMessage = L["Auth.Login.ExternalSignInStorageUnavailable"].Value;
            return;
        }

        // A full load, as the link it replaces was: the challenge is a server endpoint, not a route.
        Navigation.NavigateTo(BuildOAuthUrl(provider, oauthState), forceLoad: true);
    }
}
