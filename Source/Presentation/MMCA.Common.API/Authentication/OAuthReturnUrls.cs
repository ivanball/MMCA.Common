using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// Where an external sign-in lands when it ends, shared by <see cref="OAuthControllerBase"/> (a completion
/// that failed or succeeded) and the provider handlers' remote-failure event (a round trip that never
/// reached the completion), so both choose the destination the same way.
/// </summary>
internal static class OAuthReturnUrls
{
    /// <summary>
    /// The code a failed round trip carries back to the login page, which shows it as a readable alert.
    /// </summary>
    internal const string FailedErrorCode = "oauth_failed";

    /// <summary>The authentication-properties key the challenge stashes the client's return URL under.</summary>
    internal const string ReturnUrlItemKey = "returnUrl";

    /// <summary>
    /// Sends a provider round trip that failed before the completion (the user cancelled at the provider,
    /// the correlation cookie expired during a long wait, or the state is forged or stale) to the login page
    /// with <see cref="FailedErrorCode"/>, or to the allow-listed native callback when the challenge carried
    /// one, instead of letting the handler's exception surface as a 500.
    /// </summary>
    /// <param name="context">The remote-failure context the handler raises.</param>
    /// <returns>A completed task.</returns>
    internal static Task RedirectRemoteFailureAsync(RemoteFailureContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var uiBaseUrl = configuration[OAuthControllerBase.UIBaseUrlConfigKey]?.TrimEnd('/') ?? string.Empty;

        // A forged or stale state yields no properties, so only the web login page is possible then.
        var returnUrl = context.Properties?.GetString(ReturnUrlItemKey);
        var mobileReturnUrl = returnUrl is null ? null : GetAllowedMobileReturnUrl(configuration, returnUrl);

        context.Response.Redirect(ErrorRedirectUrl(uiBaseUrl, mobileReturnUrl, FailedErrorCode));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The redirect for a failure: the web login page normally, or the allow-listed native callback (so
    /// the WebAuthenticator window closes) when one is in play.
    /// </summary>
    /// <param name="uiBaseUrl">The configured UI base URL, without a trailing slash.</param>
    /// <param name="mobileReturnUrl">The allow-listed native callback, or <see langword="null"/>.</param>
    /// <param name="errorCode">The machine error code.</param>
    /// <returns>The redirect target.</returns>
    internal static string ErrorRedirectUrl(string uiBaseUrl, Uri? mobileReturnUrl, string errorCode) =>
        mobileReturnUrl is null
            ? $"{uiBaseUrl}/login?error={Uri.EscapeDataString(errorCode)}"
            : AppendQuery(mobileReturnUrl, $"error={Uri.EscapeDataString(errorCode)}");

    /// <summary>
    /// Returns the stashed return URL as the redirect target when it is an absolute URI whose
    /// custom scheme appears in <c>OAuth:AllowedReturnUrlSchemes</c> (ADR-043); otherwise
    /// <see langword="null"/>, which keeps the config-pinned <c>OAuth:UIBaseUrl</c> redirect.
    /// http/https URLs never match: web destinations always flow through the pinned base URL,
    /// so the allowlist cannot become an open redirect.
    /// </summary>
    /// <param name="configuration">Supplies <c>OAuth:AllowedReturnUrlSchemes</c>.</param>
    /// <param name="returnUrl">The return URL the challenge stashed.</param>
    /// <returns>The allow-listed native callback, or <see langword="null"/>.</returns>
    internal static Uri? GetAllowedMobileReturnUrl(IConfiguration configuration, string returnUrl)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Null-tolerant on purpose: a missing section, an empty section, or an IConfiguration
        // test double whose GetSection returns null must all mean "no allowlist" (the exact
        // pre-ADR-043 behavior), never a throw from ConfigurationBinder.
        var allowedSchemes = configuration.GetSection("OAuth:AllowedReturnUrlSchemes")?.Get<string[]>() ?? [];
        return allowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) ? uri : null;
    }

    /// <summary>Appends a query fragment to a native callback exactly as the client registered it.</summary>
    /// <param name="target">The native callback.</param>
    /// <param name="queryFragment">The already-escaped query fragment.</param>
    /// <returns>The callback with the fragment appended.</returns>
    internal static string AppendQuery(Uri target, string queryFragment)
    {
        // OriginalString, not ToString(): Uri normalization appends a trailing slash to
        // authority-only URIs (myapp://oauth-complete -> .../), and native authenticator
        // callback matching can be exact: echo back precisely what the client registered.
        var separator = string.IsNullOrEmpty(target.Query) ? "?" : "&";
        return $"{target.OriginalString}{separator}{queryFragment}";
    }
}
