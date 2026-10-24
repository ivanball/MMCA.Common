using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;

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
