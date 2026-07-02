using System.Security.Claims;
using System.Security.Cryptography;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MMCA.Common.API.Authentication;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// OAuth2 authentication flow for external providers (Google, GitHub), hoisted from the app hosts.
/// <para>
/// Flow: challenge endpoint → provider login page → middleware handles callback at
/// <c>/auth/callback/{provider}</c> (code exchange + state validation + cookie sign-in) →
/// redirects to <see cref="CompleteAsync"/> which reads the cookie, issues a local JWT pair via
/// <see cref="IAuthenticationService.ExternalLoginAsync"/>, stashes it under a single-use code, and
/// redirects to the UI with only that code. The UI then calls <see cref="ExchangeAsync"/> out-of-band
/// to swap the code for the tokens — so tokens are never carried in the redirect URL.
/// </para>
/// Pair with <see cref="ExternalAuthExtensions"/> (scheme registration) and an
/// <c>IAuthenticationService</c> that implements <c>ExternalLoginAsync</c>. The sealed app subclass
/// carries the class-level routing/versioning attributes (not reliably inherited):
/// <c>[ApiController][Route("auth/oauth")][ApiVersion("1.0")]</c>.
/// </summary>
public abstract class OAuthControllerBase(
    IAuthenticationService authenticationService,
    ICacheService cacheService,
    IConfiguration configuration) : ControllerBase
{
    private const string ExternalLoginScheme = ExternalAuthExtensions.ExternalLoginScheme;

    // Single-use OAuth completion codes: the redirect carries only this opaque code, while the
    // token pair waits server-side in the cache for the UI's out-of-band exchange call. Short TTL
    // because the round trip is a single redirect → page load → POST.
    private const string OAuthExchangeCodePrefix = "oauth-exchange:";
    private static readonly TimeSpan OAuthExchangeCodeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Initiates the Google OAuth2 login flow by redirecting to Google's consent screen.
    /// </summary>
    /// <param name="returnUrl">The URL to redirect to after successful authentication.</param>
    [HttpGet("google")]
    public ChallengeResult GoogleLogin([FromQuery] Uri? returnUrl = null) =>
        ChallengeProvider(GoogleDefaults.AuthenticationScheme, returnUrl);

    /// <summary>
    /// Initiates the GitHub OAuth login flow by redirecting to GitHub's authorization page.
    /// </summary>
    /// <param name="returnUrl">The URL to redirect to after successful authentication.</param>
    [HttpGet("github")]
    public ChallengeResult GitHubLogin([FromQuery] Uri? returnUrl = null) =>
        ChallengeProvider(GitHubAuthenticationDefaults.AuthenticationScheme, returnUrl);

    /// <summary>
    /// Completes the OAuth flow after the middleware has processed the provider callback.
    /// Reads external claims from the <c>ExternalLogin</c> cookie, finds or creates a local
    /// user account, issues a JWT token pair, and redirects to the UI.
    /// </summary>
    [HttpGet("complete")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> CompleteAsync()
    {
        var uiBaseUrl = configuration["OAuth:UIBaseUrl"]?.TrimEnd('/') ?? string.Empty;
        var authenticateResult = await HttpContext.AuthenticateAsync(ExternalLoginScheme);

        if (!authenticateResult.Succeeded || authenticateResult.Principal is null)
        {
            return Redirect($"{uiBaseUrl}/login?error=oauth_failed");
        }

        var (providerName, providerKey, email, firstName, lastName) = ExtractClaims(authenticateResult.Principal);

        if (string.IsNullOrEmpty(providerKey) || string.IsNullOrEmpty(email))
        {
            return Redirect($"{uiBaseUrl}/login?error=missing_claims");
        }

        var result = await authenticationService.ExternalLoginAsync(
            providerName, providerKey, email, firstName, lastName);

        if (result.IsFailure)
        {
            return RedirectToLoginWithError(uiBaseUrl, result.Errors);
        }

        var response = result.Value;
        var returnUrl = authenticateResult.Properties?.Items["returnUrl"] ?? "/";

        // Clear the temporary external login cookie
        await HttpContext.SignOutAsync(ExternalLoginScheme);

        // Mint a single-use code and stash the token pair server-side; the redirect carries only
        // the opaque code, so access/refresh tokens never land in the address bar, browser history,
        // the Referer header, or upstream access logs. The UI exchanges the code via POST below.
        var exchangeCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await cacheService.SetAsync(
            OAuthExchangeCodePrefix + exchangeCode, response, OAuthExchangeCodeLifetime, HttpContext.RequestAborted);

        return Redirect(
            $"{uiBaseUrl}/auth/oauth-complete?code={exchangeCode}&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    /// <summary>
    /// Exchanges a single-use OAuth completion code for the access/refresh token pair. Called by the
    /// UI's <c>/auth/oauth-complete</c> page out-of-band so tokens are never exposed in the redirect URL.
    /// The code is burned on first use; a missing, already-used, or expired code yields HTTP 400.
    /// </summary>
    /// <param name="request">The exchange request carrying the single-use code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("exchange")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> ExchangeAsync(
        [FromBody] OAuthCodeExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return InvalidCode();
        }

        var cacheKey = OAuthExchangeCodePrefix + request.Code;

        // AuthenticationResponse is a struct, so a cache miss yields default(AuthenticationResponse)
        // (null AccessToken) rather than null — detect the miss via the token, matching AuthUIService.
        var response = await cacheService.GetAsync<AuthenticationResponse>(cacheKey, cancellationToken);
        if (string.IsNullOrEmpty(response.AccessToken))
        {
            return InvalidCode();
        }

        // Single-use: burn the code so a leaked or replayed code can't mint a second token pair.
        await cacheService.RemoveAsync(cacheKey, cancellationToken);

        return Ok(response);
    }

    private BadRequestObjectResult InvalidCode() =>
        BadRequest(new ProblemDetails
        {
            Status = 400,
            Title = "Invalid sign-in code",
            Detail = "The sign-in code is invalid or has expired.",
        });

    private static (string ProviderName, string? ProviderKey, string? Email, string FirstName, string LastName)
        ExtractClaims(ClaimsPrincipal claims)
    {
        var providerName = claims.Identity?.AuthenticationType ?? "Unknown";
        var providerKey = claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = claims.FindFirst(ClaimTypes.Email)?.Value;
        var (firstName, lastName) = ExtractName(claims);
        return (providerName, providerKey, email, firstName, lastName);
    }

    private static (string FirstName, string LastName) ExtractName(ClaimsPrincipal claims)
    {
        var givenName = claims.FindFirst(ClaimTypes.GivenName)?.Value;
        var surname = claims.FindFirst(ClaimTypes.Surname)?.Value;

        if (givenName is not null && surname is not null)
        {
            return (givenName, surname);
        }

        var (fallbackFirst, fallbackLast) = SplitFullName(claims.FindFirst(ClaimTypes.Name)?.Value);
        return (givenName ?? fallbackFirst, surname ?? fallbackLast);
    }

    private static (string First, string Last) SplitFullName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return ("User", string.Empty);
        }

        var spaceIndex = fullName.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIndex <= 0)
        {
            return ("User", string.Empty);
        }

        return (fullName[..spaceIndex], fullName[(spaceIndex + 1)..]);
    }

    private RedirectResult RedirectToLoginWithError(string uiBaseUrl, IReadOnlyCollection<Error> errors)
    {
        var errorCode = errors.Count > 0 ? errors.First().Code : "unknown";
        return Redirect($"{uiBaseUrl}/login?error={Uri.EscapeDataString(errorCode)}");
    }

    private ChallengeResult ChallengeProvider(string scheme, Uri? returnUrl)
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/auth/oauth/complete",
            Items = { ["returnUrl"] = returnUrl?.ToString() ?? "/" }
        };
        return Challenge(properties, scheme);
    }
}
