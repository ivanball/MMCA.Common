using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Shared.Auth;

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
    AuthenticationStateProvider authStateProvider) : IAuthUIService
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

    public async Task LogoutAsync()
    {
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
        var accessToken = await tokenStorageService.GetAccessTokenAsync();
        var refreshToken = await tokenStorageService.GetRefreshTokenAsync();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        var request = new RefreshTokenRequest(accessToken, refreshToken);
        var response = await httpClient.PostAsJsonAsync(new Uri("auth/refresh", UriKind.Relative), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
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

            return false;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return false;
        }

        try
        {
            await tokenStorageService.SetTokensAsync(result.AccessToken, result.RefreshToken);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (authStateProvider is JwtAuthenticationStateProvider jwtProvider2)
        {
            jwtProvider2.NotifyUserAuthentication(result.AccessToken);
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
