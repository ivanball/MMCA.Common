using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the <c>/login</c> page: the external sign-in refusal it is sent back with.
/// </summary>
public partial class Login
{
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
    /// Writes the per-attempt OAuth state the completion page requires back. When device storage
    /// accepts the attempt without keeping it (storage full, site data blocked, private mode) the
    /// completion would refuse the flow one redirect later with no explanation, so this says so now
    /// and keeps the web-redirect providers disabled.
    /// </summary>
    private async Task BeginOAuthAttemptAsync()
    {
        try
        {
            _oauthState = await OAuthFlowState.BeginAsync();
        }
        catch (InvalidOperationException)
        {
            _externalSignInUnavailable = true;
            _errorMessage = L["Auth.Login.ExternalSignInStorageUnavailable"].Value;
            StateHasChanged();
            return;
        }

        if (_oauthState is not null)
        {
            StateHasChanged();
        }
    }
}
