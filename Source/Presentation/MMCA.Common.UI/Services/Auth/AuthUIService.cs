using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Caching;
using MMCA.Common.UI.Services.Capabilities.Notifications;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Implements <see cref="IAuthUIService"/> by calling the WebAPI <c>auth/*</c> endpoints,
/// persisting tokens via <see cref="ITokenStorageService"/>, and pushing auth-state changes
/// through <see cref="JwtAuthenticationStateProvider"/> so Blazor's <c>AuthorizeView</c> reacts instantly.
/// <para>
/// Failures come back as <see cref="Result"/> values read from the API's Problem Details payload
/// (<see cref="ProblemDetailsResultReader"/>), so the caller sees the server's own error text and
/// <see cref="ErrorType"/>. Storing tokens can also fail locally when JS interop is unavailable
/// (SSR prerender, a render-mode transition); that is reported as
/// <see cref="TokenStorageUnavailableCode"/> rather than swallowed into a null.
/// </para>
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c> HttpClient.</param>
/// <param name="tokenStorageService">Store the access/refresh tokens are persisted in.</param>
/// <param name="tokenRefresher">Host-specific refresher used to renew an expired access token.</param>
/// <param name="authStateProvider">Blazor's auth-state provider, notified on sign-in and sign-out.</param>
/// <param name="pushRegistration">Native push registration, unregistered on sign-out (ADR-044).</param>
/// <param name="readCache">
/// Optional client-side read cache, emptied on sign-out. It matters most where the DI scope outlives
/// the session (WebAssembly and MAUI resolve one scope for the app's lifetime): without the clear, the
/// next account signing in on the same client could be served the previous account's cached reads.
/// </param>
public sealed class AuthUIService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    ITokenRefresher tokenRefresher,
    AuthenticationStateProvider authStateProvider,
    IPushRegistrationService pushRegistration,
    IUiReadCache? readCache = null) : IAuthUIService
{
    /// <summary>
    /// Error code reported when the authentication succeeded but the tokens could not be persisted
    /// because JS interop was not available (SSR prerender or a render-mode transition).
    /// </summary>
    public const string TokenStorageUnavailableCode = "Auth.TokenStorageUnavailable";

    /// <summary>
    /// Error code reported when the API answered 2xx with no usable access token, which no live
    /// server does; it means the response shape drifted.
    /// </summary>
    public const string MissingAccessTokenCode = "Auth.MissingAccessToken";

    private const string ApiClientName = "APIClient";

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        await AuthenticateAsync("auth/login", request, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        await AuthenticateAsync("auth/register", request, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Validation("Auth.OAuth.MissingCode", "Authentication failed: missing exchange code."));
        }

        return await AuthenticateAsync("auth/oauth/exchange", new OAuthCodeExchangeRequest(code), cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogoutAsync()
    {
        // Native push (ADR-044): drop this device's installation while the access token is
        // still valid: the Devices DELETE is authenticated. No-op on web heads. Best-effort:
        // a failure must never block sign-out.
        try
        {
            await pushRegistration.UnregisterAsync();
        }
#pragma warning disable CA1031 // Do not catch general exception types: unregistration is best-effort
        catch
#pragma warning restore CA1031
        {
            // Ignore errors - we still want to sign out locally.
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);

        var accessToken = await ReadAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Best-effort revoke on the server side. Deliberately fire-and-forget: the local
            // sign-out below runs whatever happened, so a dropped connection cannot strand a user
            // inside a session they asked to leave.
            try
            {
                await httpClient.PostAsync(new Uri("auth/revoke", UriKind.Relative), null);
            }
#pragma warning disable CA1031 // Do not catch general exception types: the revoke is best-effort
            catch
#pragma warning restore CA1031
            {
                // Ignore errors - we still want to clear local tokens
            }
        }

        try
        {
            await tokenStorageService.ClearTokensAsync();
        }
        catch (InvalidOperationException)
        {
            // JS interop not available
        }

        // Everything the previous session read is now another user's data. The scope that holds the
        // cache outlives the session on WebAssembly and MAUI, so leaving entries behind would show
        // them to whoever signs in next on this client.
        readCache?.Clear();

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserLogout();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        // Delegates to the host-specific refresher: browser hosts refresh via the same-origin cookie proxy
        // (refresh token stays server-side); MAUI refreshes directly from SecureStorage. A null result means
        // the session can no longer be refreshed (missing/expired/revoked credential) -> treat as logout.
        var accessToken = await tokenRefresher.AcquireAccessTokenAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                await tokenStorageService.ClearTokensAsync();
            }
            catch (InvalidOperationException)
            {
                // JS interop not available (SSR prerender / disconnected circuit)
            }

            // The same reasoning as LogoutAsync: an unrefreshable session IS a sign-out, and the
            // cached reads belong to the session that just ended.
            readCache?.Clear();

            if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
            {
                jwtProvider.NotifyUserLogout();
            }

            return false;
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider2)
        {
            jwtProvider2.NotifyUserAuthentication(accessToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<Result> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                var request = new ChangePasswordRequest(currentPassword, newPassword);
                using var response = await httpClient.PutAsJsonAsync(
                    new Uri("auth/password", UriKind.Relative), request, cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                // Anonymous endpoint: no bearer header is attached, matching LoginAsync/RegisterAsync. A
                // signed-in caller must not have the reset bound to their current session either.
                using var httpClient = httpClientFactory.CreateClient(ApiClientName);
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri("auth/forgot-password", UriKind.Relative), new ForgotPasswordRequest(email), cancellationToken);

                // The endpoint answers 202 for every well-formed address, so a failure here is a
                // malformed request or an outage, never a signal about whether the account exists.
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = httpClientFactory.CreateClient(ApiClientName);
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri("auth/reset-password", UriKind.Relative),
                    new ResetPasswordRequest(email, token, newPassword),
                    cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.GetAsync(
                    new Uri("auth/my-sessions", UriKind.Relative), cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync<IReadOnlyList<RefreshSessionSummaryResponse>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                var url = $"auth/revoke/{sessionId}";

                // The endpoint answers 204, so the non-generic reader is the right one: asking for a
                // value here would turn every success into an "empty response" failure.
                using var response = await httpClient.PostAsync(new Uri(url, UriKind.Relative), content: null, cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);

    /// <summary>
    /// The shared body of login, register and OAuth exchange: post the credential, read the token
    /// pair back, persist it, and tell Blazor's auth state about it. Every step that can fail
    /// contributes its own error rather than collapsing into a null.
    /// </summary>
    private async Task<Result<AuthenticationResponse>> AuthenticateAsync(
        string relativeUrl,
        object request,
        CancellationToken cancellationToken)
    {
        var result = await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = httpClientFactory.CreateClient(ApiClientName);
                using var response = await httpClient.PostAsJsonAsync(new Uri(relativeUrl, UriKind.Relative), request, cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync<AuthenticationResponse>(response, cancellationToken: cancellationToken);
            },
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result;
        }

        var authentication = result.Value;
        if (string.IsNullOrWhiteSpace(authentication.AccessToken))
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Unexpected(MissingAccessTokenCode, "The sign-in response carried no access token."));
        }

        try
        {
            await tokenStorageService.SetTokensAsync(authentication.AccessToken, authentication.RefreshToken);
        }
        catch (InvalidOperationException exception)
        {
            // JS interop not available (e.g., during render mode transition): the credentials are
            // valid but nothing can hold them, so this is a failure, not a silent no-op.
            return Result.Failure<AuthenticationResponse>(
                Error.Unexpected(
                    TokenStorageUnavailableCode,
                    "The session could not be stored on this device. Try again.",
                    exception.Message));
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserAuthentication(authentication.AccessToken);
        }

        return result;
    }

    /// <summary>
    /// Creates an APIClient carrying the stored access token. Mirrors
    /// <c>AuthenticatedServiceBase.CreateAuthenticatedClientAsync</c>; this service predates that
    /// base class and cannot inherit it (it is not an entity service and takes a different set of
    /// dependencies).
    /// </summary>
    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var httpClient = httpClientFactory.CreateClient(ApiClientName);

        var token = await ReadAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return httpClient;
    }

    private async Task<string?> ReadAccessTokenAsync()
    {
        try
        {
            return await tokenStorageService.GetAccessTokenAsync();
        }
        catch (InvalidOperationException)
        {
            // JS interop not available during SSR prerender: proceed without a token and let the
            // API answer 401, which the caller renders like any other failure.
            return null;
        }
    }
}
