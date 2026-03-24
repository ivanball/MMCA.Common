using System.Net.Http.Headers;
using MMCA.UI.Shared.Services.Auth;
using Polly;
using Polly.Retry;

namespace MMCA.UI.Shared.Services;

/// <summary>
/// Shared base class for HTTP services that need authenticated API calls.
/// Provides a Polly retry policy (exponential backoff, 3 retries on transient/5xx errors)
/// and a helper to create an <see cref="HttpClient"/> with the JWT Bearer token from
/// the circuit-scoped <see cref="ITokenStorageService"/>.
/// </summary>
public abstract class AuthenticatedServiceBase(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
{
    /// <summary>
    /// Polly retry policy: retries on <see cref="HttpRequestException"/> or 5xx status codes,
    /// with exponential backoff (2s, 4s, 8s).
    /// </summary>
    protected static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ITokenStorageService _tokenStorageService = tokenStorageService ?? throw new ArgumentNullException(nameof(tokenStorageService));

    /// <summary>
    /// Creates an HttpClient with the JWT Bearer token applied from the circuit-scoped
    /// <see cref="ITokenStorageService"/>. This bypasses the <see cref="Auth.AuthDelegatingHandler"/>
    /// scope issue where <c>IHttpClientFactory</c> creates handlers in a separate DI scope
    /// that cannot access the Blazor circuit's <c>IJSRuntime</c> for localStorage reads.
    /// </summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var httpClient = _httpClientFactory.CreateClient("APIClient");

        try
        {
            var token = await _tokenStorageService.GetAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch (InvalidOperationException)
        {
            // JS interop not available during SSR prerender — proceed without token
        }

        return httpClient;
    }
}
