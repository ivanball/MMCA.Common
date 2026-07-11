using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Implements <see cref="IAuthUIService"/> by calling the WebAPI <c>auth/*</c> endpoints,
/// persisting tokens via <see cref="ITokenStorageService"/>, and pushing auth-state changes
/// through <see cref="JwtAuthenticationStateProvider"/> so Blazor's <c>AuthorizeView</c> reacts instantly.
/// InvalidOperationException catches guard against JS-interop failures during SSR prerender.
/// </summary>
public sealed class AuthUIService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    ITokenRefresher tokenRefresher,
    AuthenticationStateProvider authStateProvider,
    IPushRegistrationService pushRegistration) : IAuthUIService
{
    private const string ApiClientName = "APIClient";

    public string? LastError { get; private set; }

    public async Task<AuthenticationResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        LastError = null;
        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        var response = await httpClient.PostAsJsonAsync(new Uri("auth/login", UriKind.Relative), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
                LastError = problem?.Detail;
            }
            catch
            {
                // Response body may not be valid ProblemDetails
            }

            LastError ??= "Login failed. Please try again.";
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return null;
        }

        try
        {
            await tokenStorageService.SetTokensAsync(result.AccessToken, result.RefreshToken);
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during render mode transition)
            return null;
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserAuthentication(result.AccessToken);
        }

        return result;
    }

    public async Task<AuthenticationResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        LastError = null;
        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        var response = await httpClient.PostAsJsonAsync(new Uri("auth/register", UriKind.Relative), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
                LastError = problem?.Detail;
            }
            catch
            {
                // Response body may not be valid ProblemDetails
            }

            LastError ??= "Registration failed. Please try again.";
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return null;
        }

        try
        {
            await tokenStorageService.SetTokensAsync(result.AccessToken, result.RefreshToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserAuthentication(result.AccessToken);
        }

        return result;
    }

    public async Task<AuthenticationResponse?> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(code))
        {
            LastError = "Authentication failed: missing exchange code.";
            return null;
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        var response = await httpClient.PostAsJsonAsync(
            new Uri("auth/oauth/exchange", UriKind.Relative), new OAuthCodeExchangeRequest(code), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
                LastError = problem?.Detail;
            }
            catch
            {
                // Response body may not be valid ProblemDetails
            }

            LastError ??= "Sign in could not be completed. Please try again.";
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            LastError = "Sign in could not be completed. Please try again.";
            return null;
        }

        try
        {
            await tokenStorageService.SetTokensAsync(result.AccessToken, result.RefreshToken);
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during render mode transition)
            return null;
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserAuthentication(result.AccessToken);
        }

        return result;
    }

    public async Task LogoutAsync()
    {
        // Native push (ADR-044): drop this device's installation while the access token is
        // still valid — the Devices DELETE is authenticated. No-op on web heads. Best-effort:
        // a failure must never block sign-out.
        try
        {
            await pushRegistration.UnregisterAsync();
        }
#pragma warning disable CA1031 // Do not catch general exception types — unregistration is best-effort
        catch
#pragma warning restore CA1031
        {
            // Ignore errors - we still want to sign out locally.
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);

        var accessToken = await tokenStorageService.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            // Best-effort revoke on server side
            try
            {
                await httpClient.PostAsync(new Uri("auth/revoke", UriKind.Relative), null);
            }
            catch
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

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider)
        {
            jwtProvider.NotifyUserLogout();
        }
    }

    public async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        // Delegates to the host-specific refresher: browser hosts refresh via the same-origin cookie proxy
        // (refresh token stays server-side); MAUI refreshes directly from SecureStorage. A null result means
        // the session can no longer be refreshed (missing/expired/revoked credential) → treat as logout.
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

    public async Task<bool> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = httpClientFactory.CreateClient(ApiClientName);

        // Apply token from circuit-scoped storage (AuthDelegatingHandler has scope issues)
        try
        {
            var token = await tokenStorageService.GetAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch (InvalidOperationException)
        {
            // JS interop not available during SSR prerender
        }

        var request = new ChangePasswordRequest(currentPassword, newPassword);
        var response = await httpClient.PutAsJsonAsync(
            new Uri("auth/password", UriKind.Relative), request, cancellationToken);

        return response.IsSuccessStatusCode;
    }
}
