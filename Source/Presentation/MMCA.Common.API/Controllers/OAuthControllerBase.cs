using System.Security.Claims;
using System.Security.Cryptography;
using AspNet.Security.OAuth.Apple;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MMCA.Common.API.Authentication;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// OAuth2 authentication flow for external providers (Google, GitHub, Apple), hoisted from the app hosts.
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
/// <para>
/// Multi-replica hosts: give the subclass an <see cref="IDistributedLock"/> constructor parameter and
/// pass it to the four-argument base constructor (<c>AddCaching</c> registers a Redis-backed lock when
/// an <c>IConnectionMultiplexer</c> is present). <see cref="ExchangeAsync"/> then reads and burns the
/// single-use code inside that lock, so two replicas redeeming the same code at the same instant
/// cannot both mint the token pair. A subclass that keeps the three-argument constructor gets no lock
/// and the unlocked read-then-burn, which is single-use only per replica.
/// </para>
/// </summary>
/// <param name="authenticationService">Issues the local token pair for an external sign-in.</param>
/// <param name="cacheService">Holds the single-use exchange codes.</param>
/// <param name="configuration">Supplies the <c>OAuth</c> settings (UI base URL, allowed return schemes).</param>
/// <param name="distributedLock">
/// The lock that makes the code exchange atomic across replicas, or <see langword="null"/> to redeem
/// without one.
/// </param>
public abstract class OAuthControllerBase(
    IAuthenticationService authenticationService,
    ICacheService cacheService,
    IConfiguration configuration,
    IDistributedLock? distributedLock) : ControllerBase
{
    private const string ExternalLoginScheme = ExternalAuthExtensions.ExternalLoginScheme;

    // Single-use OAuth completion codes: the redirect carries only this opaque code, while the
    // token pair waits server-side in the cache for the UI's out-of-band exchange call. Short TTL
    // because the round trip is a single redirect → page load → POST.
    private const string OAuthExchangeCodePrefix = "oauth-exchange:";

    // Authentication-properties key the client's opaque per-attempt state rides in. Not "state":
    // that name belongs to the OAuth handler's own protocol value.
    private const string ClientStateItemKey = "clientState";

    // The GitHub handler maps ClaimTypes.Name to the login handle and the profile display name here.
    private const string GitHubDisplayNameClaimType = "urn:github:name";

    // Stand-ins for a name the provider did not supply. Never empty: user invariants reject an
    // empty first or last name, so an empty value fails the first external sign-up outright.
    private const string PlaceholderFirstName = "User";
    private const string PlaceholderLastName = "User";

    private static readonly TimeSpan OAuthExchangeCodeLifetime = TimeSpan.FromMinutes(2);

    // Lease and wait for the exchange-code redeem lock, matching PasswordResetTokenService: the
    // critical section is one cache read and one remove, so ten seconds is far past its length, and a
    // caller that cannot get the lock within five is answered as an invalid code.
    private static readonly TimeSpan RedeemLockTimeToLive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RedeemLockWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthControllerBase"/> class without a distributed
    /// lock: the exchange code is read and burned unlocked, which is single-use per replica only.
    /// </summary>
    /// <param name="authenticationService">Issues the local token pair for an external sign-in.</param>
    /// <param name="cacheService">Holds the single-use exchange codes.</param>
    /// <param name="configuration">Supplies the <c>OAuth</c> settings.</param>
    protected OAuthControllerBase(
        IAuthenticationService authenticationService,
        ICacheService cacheService,
        IConfiguration configuration)
        : this(authenticationService, cacheService, configuration, distributedLock: null)
    {
    }

    /// <summary>
    /// Initiates the Google OAuth2 login flow by redirecting to Google's consent screen.
    /// </summary>
    /// <param name="returnUrl">The URL to redirect to after successful authentication.</param>
    /// <param name="state">Opaque per-attempt value the client round-trips to bind the completion to the flow it started.</param>
    [HttpGet("google")]
    [AllowAnonymous]
    public ChallengeResult GoogleLogin([FromQuery] Uri? returnUrl = null, [FromQuery] string? state = null) =>
        ChallengeProvider(GoogleDefaults.AuthenticationScheme, returnUrl, state);

    /// <summary>
    /// Initiates the GitHub OAuth login flow by redirecting to GitHub's authorization page.
    /// </summary>
    /// <param name="returnUrl">The URL to redirect to after successful authentication.</param>
    /// <param name="state">Opaque per-attempt value the client round-trips to bind the completion to the flow it started.</param>
    [HttpGet("github")]
    [AllowAnonymous]
    public ChallengeResult GitHubLogin([FromQuery] Uri? returnUrl = null, [FromQuery] string? state = null) =>
        ChallengeProvider(GitHubAuthenticationDefaults.AuthenticationScheme, returnUrl, state);

    /// <summary>
    /// Initiates the Sign in with Apple flow by redirecting to Apple's authorization page.
    /// Apple returns the callback as a cross-site form POST (response_mode=form_post is forced
    /// by the name/email scopes), which the middleware handles at <c>/auth/callback/apple</c>
    /// like any other provider callback.
    /// </summary>
    /// <param name="returnUrl">The URL to redirect to after successful authentication.</param>
    /// <param name="state">Opaque per-attempt value the client round-trips to bind the completion to the flow it started.</param>
    [HttpGet("apple")]
    [AllowAnonymous]
    public ChallengeResult AppleLogin([FromQuery] Uri? returnUrl = null, [FromQuery] string? state = null) =>
        ChallengeProvider(AppleAuthenticationDefaults.AuthenticationScheme, returnUrl, state);

    /// <summary>
    /// Completes the OAuth flow after the middleware has processed the provider callback.
    /// Reads external claims from the <c>ExternalLogin</c> cookie, finds or creates a local
    /// user account, issues a JWT token pair, and redirects to the UI.
    /// <para>
    /// Native heads (ADR-043): when the stashed <c>returnUrl</c> uses a custom scheme listed in
    /// <c>OAuth:AllowedReturnUrlSchemes</c> (e.g. <c>myapp://oauth-complete</c>), the redirect
    /// targets that URL instead of <c>OAuth:UIBaseUrl</c>, so the system-browser
    /// <c>WebAuthenticator</c> window captures the single-use code and closes. Both redirects carry
    /// the single-use <c>code</c> and, when the challenge stashed one, the client's opaque
    /// <c>state</c> value echoed back, so the client can bind the completion to the attempt it
    /// started. An empty allowlist (the default) preserves the web-only behavior exactly.
    /// </para>
    /// </summary>
    [HttpGet("complete")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> CompleteAsync()
    {
        var uiBaseUrl = configuration["OAuth:UIBaseUrl"]?.TrimEnd('/') ?? string.Empty;
        var authenticateResult = await HttpContext.AuthenticateAsync(ExternalLoginScheme).ConfigureAwait(false);

        if (!authenticateResult.Succeeded || authenticateResult.Principal is null)
        {
            // No authentication properties survive this failure, so the stashed returnUrl is
            // unavailable — the web login page is the only possible destination.
            return Redirect($"{uiBaseUrl}/login?error=oauth_failed");
        }

        // Safe lookup (GetString is TryGetValue under the hood): the challenge normally stashes
        // returnUrl, but a ticket minted without it (custom challenge, provider round-trip edge)
        // must fall back to "/" instead of throwing KeyNotFoundException on the Items indexer.
        var (returnUrl, clientState) = ReadChallengeState(authenticateResult.Properties);
        var mobileReturnUrl = GetAllowedMobileReturnUrl(returnUrl);

        var (providerName, providerKey, email, firstName, lastName) = ExtractClaims(authenticateResult.Principal);

        if (string.IsNullOrEmpty(providerKey) || string.IsNullOrEmpty(email))
        {
            return RedirectError(uiBaseUrl, mobileReturnUrl, "missing_claims");
        }

        var result = await authenticationService.ExternalLoginAsync(
            providerName, providerKey, email, firstName, lastName).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return RedirectError(uiBaseUrl, mobileReturnUrl, GetErrorCode(result.Errors));
        }

        var response = result.Value;

        // Clear the temporary external login cookie
        await HttpContext.SignOutAsync(ExternalLoginScheme).ConfigureAwait(false);

        // Mint a single-use code and stash the token pair server-side; the redirect carries only
        // the opaque code, so access/refresh tokens never land in the address bar, browser history,
        // the Referer header, or upstream access logs. The UI exchanges the code via POST below.
        var exchangeCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await cacheService.SetAsync(
            OAuthExchangeCodePrefix + exchangeCode, response, OAuthExchangeCodeLifetime, HttpContext.RequestAborted).ConfigureAwait(false);

        return Redirect(BuildSuccessRedirectUrl(uiBaseUrl, mobileReturnUrl, exchangeCode, returnUrl, clientState));
    }

    // Safe lookups (GetString is TryGetValue under the hood): the challenge normally stashes both,
    // but a ticket minted without them (custom challenge, provider round-trip edge) must fall back
    // instead of throwing on the Items indexer.
    private static (string ReturnUrl, string? ClientState) ReadChallengeState(AuthenticationProperties? properties) =>
        (properties?.GetString("returnUrl") ?? "/", properties?.GetString(ClientStateItemKey));

    private static string BuildSuccessRedirectUrl(
        string uiBaseUrl,
        Uri? mobileReturnUrl,
        string exchangeCode,
        string returnUrl,
        string? clientState)
    {
        var stateSuffix = string.IsNullOrEmpty(clientState)
            ? string.Empty
            : $"&state={Uri.EscapeDataString(clientState)}";

        return mobileReturnUrl is null
            ? $"{uiBaseUrl}/auth/oauth-complete?code={exchangeCode}&returnUrl={Uri.EscapeDataString(returnUrl)}{stateSuffix}"
            : AppendQuery(mobileReturnUrl, $"code={exchangeCode}{stateSuffix}");
    }

    /// <summary>
    /// Exchanges a single-use OAuth completion code for the access/refresh token pair. Called by the
    /// UI's <c>/auth/oauth-complete</c> page out-of-band so tokens are never exposed in the redirect URL.
    /// The code is burned on first use; a missing, already-used, or expired code yields HTTP 400.
    /// <para>
    /// With a distributed lock (the four-argument constructor), the read and the burn run inside
    /// <c>lock:oauth-exchange:{code}</c>, so a code is redeemed once across every replica; a redeem
    /// that cannot take the lock within five seconds also yields the HTTP 400 invalid-code answer.
    /// </para>
    /// </summary>
    /// <param name="request">The exchange request carrying the single-use code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("exchange")]
    [NonIdempotent("The exchange burns a single-use code and hands back a token pair. Replaying the stored response would defeat the burn, letting a leaked code mint the same tokens again for the whole retention window.")]
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

        if (distributedLock is null)
        {
            return await RedeemExchangeCodeAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }

        // The read and the burn are one critical section: without it, two replicas that both read
        // before either removes would both hand out the token pair. A contended redeem is answered
        // as an invalid code, never as a second success.
        var handle = await distributedLock
            .TryAcquireAsync($"lock:{cacheKey}", RedeemLockTimeToLive, RedeemLockWait, cancellationToken)
            .ConfigureAwait(false);
        if (handle is null)
        {
            return InvalidCode();
        }

        await using (handle.ConfigureAwait(false))
        {
            return await RedeemExchangeCodeAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IActionResult> RedeemExchangeCodeAsync(string cacheKey, CancellationToken cancellationToken)
    {
        // AuthenticationResponse is a struct, so a cache miss yields default(AuthenticationResponse)
        // (null AccessToken) rather than null — detect the miss via the token, matching AuthUIService.
        // Read from the shared store: a code already burned on another replica must be a miss here,
        // not a stale local copy that would mint a second token pair.
        var response = await cacheService.GetFromSharedStoreAsync<AuthenticationResponse>(cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.AccessToken))
        {
            return InvalidCode();
        }

        // Single-use: burn the code so a leaked or replayed code can't mint a second token pair.
        await cacheService.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);

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

        if (!string.IsNullOrWhiteSpace(givenName) && !string.IsNullOrWhiteSpace(surname))
        {
            return (givenName, surname);
        }

        // GitHub maps ClaimTypes.Name to the LOGIN handle and puts the profile display name in
        // "urn:github:name", so the display-name claim wins whenever the provider issues one.
        var fullName = claims.FindFirst(GitHubDisplayNameClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            fullName = claims.FindFirst(ClaimTypes.Name)?.Value;
        }

        var (fallbackFirst, fallbackLast) = SplitFullName(fullName);
        var firstName = string.IsNullOrWhiteSpace(givenName) ? fallbackFirst : givenName;
        var lastName = string.IsNullOrWhiteSpace(surname) ? fallbackLast : surname;
        return (firstName, lastName);
    }

    /// <summary>
    /// Splits a display name at its first space. A missing or single-token name yields the
    /// placeholders, never an empty last name: user invariants reject an empty last name, which
    /// would bounce every such first sign-up back to the login page.
    /// </summary>
    private static (string First, string Last) SplitFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return (PlaceholderFirstName, PlaceholderLastName);
        }

        var trimmed = fullName.Trim();
        var spaceIndex = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIndex <= 0)
        {
            return (PlaceholderFirstName, PlaceholderLastName);
        }

        var last = trimmed[(spaceIndex + 1)..].Trim();
        return (trimmed[..spaceIndex], last.Length == 0 ? PlaceholderLastName : last);
    }

    private static string GetErrorCode(IReadOnlyList<Error> errors) =>
        errors.Count > 0 ? errors[0].Code : "unknown";

    private RedirectResult RedirectToLoginWithError(string uiBaseUrl, string errorCode) =>
        Redirect($"{uiBaseUrl}/login?error={Uri.EscapeDataString(errorCode)}");

    /// <summary>
    /// Redirects a completion failure to the right surface: the web login page normally, or the
    /// allow-listed native callback (so the WebAuthenticator window closes) when one is in play.
    /// </summary>
    private RedirectResult RedirectError(string uiBaseUrl, Uri? mobileReturnUrl, string errorCode) =>
        mobileReturnUrl is null
            ? RedirectToLoginWithError(uiBaseUrl, errorCode)
            : Redirect(AppendQuery(mobileReturnUrl, $"error={Uri.EscapeDataString(errorCode)}"));

    /// <summary>
    /// Returns the stashed return URL as the redirect target when it is an absolute URI whose
    /// custom scheme appears in <c>OAuth:AllowedReturnUrlSchemes</c> (ADR-043); otherwise
    /// <see langword="null"/>, which keeps the config-pinned <c>OAuth:UIBaseUrl</c> redirect.
    /// http/https URLs never match — web destinations always flow through the pinned base URL,
    /// so the allowlist cannot become an open redirect.
    /// </summary>
    private Uri? GetAllowedMobileReturnUrl(string returnUrl)
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

    private static string AppendQuery(Uri target, string queryFragment)
    {
        // OriginalString, not ToString(): Uri normalization appends a trailing slash to
        // authority-only URIs (myapp://oauth-complete -> .../), and native authenticator
        // callback matching can be exact — echo back precisely what the client registered.
        var separator = string.IsNullOrEmpty(target.Query) ? "?" : "&";
        return $"{target.OriginalString}{separator}{queryFragment}";
    }

    private ChallengeResult ChallengeProvider(string scheme, Uri? returnUrl, string? clientState)
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/auth/oauth/complete",
            Items =
            {
                ["returnUrl"] = returnUrl?.ToString() ?? "/",

                // SECURITY: carried through the provider round trip and handed back on the
                // completion redirect so the client can prove the code belongs to the flow IT
                // started. The value is opaque here: the server never interprets or trusts it.
                [ClientStateItemKey] = clientState,
            }
        };
        return Challenge(properties, scheme);
    }
}
