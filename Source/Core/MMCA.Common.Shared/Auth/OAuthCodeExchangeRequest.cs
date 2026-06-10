namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for exchanging a short-lived, single-use OAuth completion code for the
/// authentication token pair. The code is minted server-side after the external provider
/// callback succeeds and carried to the UI in the redirect URL instead of the tokens
/// themselves — so access/refresh tokens never appear in the address bar, browser history,
/// the <c>Referer</c> header, or server access logs.
/// </summary>
/// <param name="Code">The opaque, single-use exchange code issued by the OAuth complete endpoint.</param>
public readonly record struct OAuthCodeExchangeRequest(
    string Code);
