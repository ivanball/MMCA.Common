using System.Net;
using System.Net.Http.Headers;
using MMCA.Common.UI.Services.Auth;
using Polly;
using Polly.Retry;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Shared base class for HTTP services that need authenticated API calls.
/// Provides a Polly retry policy (exponential backoff with jitter, 3 retries on transient
/// failures) and a helper to create an <see cref="HttpClient"/> with the JWT Bearer token from
/// the circuit-scoped <see cref="ITokenStorageService"/>.
/// </summary>
public abstract class AuthenticatedServiceBase(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
{
    /// <summary>
    /// Polly retry policy: 3 retries on <see cref="HttpRequestException"/> or a retryable response
    /// status (see <c>IsRetryableResponse</c>), with exponential backoff (2s, 4s, 8s) plus up to
    /// one second of random jitter so a fleet of clients does not re-converge on the same instant.
    /// Every retried response is disposed; the caller owns the final one.
    /// </summary>
    protected static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = BuildRetryPolicy(DefaultBackoff);

    private const string ApiClientName = "APIClient";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ITokenStorageService _tokenStorageService = tokenStorageService ?? throw new ArgumentNullException(nameof(tokenStorageService));

    /// <summary>
    /// Creates a fresh idempotency key for one logical write operation.
    /// </summary>
    /// <remarks>
    /// The value is generated ONCE per logical operation and then reused across every retry
    /// attempt of that operation. That reuse is the whole point: the server-side idempotency
    /// filter keys its cached response off this value, so a retried create collapses into a single
    /// execution plus a replayed response. Generating a new key per attempt would defeat the
    /// dedup entirely and let a retry create a duplicate record.
    /// </remarks>
    /// <returns>A new key: a GUID in compact ("N") form.</returns>
    protected static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates an HttpClient with the JWT Bearer token applied from the circuit-scoped
    /// <see cref="ITokenStorageService"/>. This bypasses the <see cref="Auth.AuthDelegatingHandler"/>
    /// scope issue where <c>IHttpClientFactory</c> creates handlers in a separate DI scope
    /// that cannot access the Blazor circuit's <c>IJSRuntime</c> to read the in-memory access token.
    /// </summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var httpClient = _httpClientFactory.CreateClient(ApiClientName);

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
            // JS interop not available during SSR prerender: proceed without token
        }

        return httpClient;
    }

    /// <summary>
    /// Creates an APIClient carrying an explicit bearer token rather than the one currently held by
    /// <see cref="Auth.ITokenStorageService"/>. Used to replay a request the API answered
    /// <c>401 Unauthorized</c> with a token acquired straight from
    /// <see cref="Auth.ITokenRefresher"/>: the stored token still looks fresh by the client clock,
    /// so re-reading storage would just resend the token the server has already rejected.
    /// </summary>
    /// <param name="accessToken">The freshly acquired access token.</param>
    protected HttpClient CreateClientWithToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var httpClient = _httpClientFactory.CreateClient(ApiClientName);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return httpClient;
    }

    /// <summary>
    /// Decides whether a response is worth another attempt: server-side failures (5xx) except the
    /// two that are permanent verdicts about the request itself, plus the two explicit
    /// "come back later" codes.
    /// </summary>
    /// <remarks>
    /// 501 (Not Implemented) and 505 (HTTP Version Not Supported) are permanent: the endpoint or
    /// protocol will not start working within the retry window, so retrying only burns the budget
    /// and delays the error the caller needs to see. 408 (Request Timeout) and 429 (Too Many
    /// Requests) are the server explicitly inviting a later attempt, which the backoff supplies.
    /// </remarks>
    private static bool IsRetryableResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported)
        {
            return false;
        }

        return (int)response.StatusCode >= 500
            || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
    }

#pragma warning disable S2245, CA5394 // Random only spaces retry attempts apart (jitter); it feeds no security, token, key or cryptographic decision, so a pseudorandom generator is the correct tool here.

    /// <summary>The shipped backoff: 2s, 4s, 8s plus up to one second of jitter.</summary>
    private static TimeSpan DefaultBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt))
            + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
#pragma warning restore S2245, CA5394

    /// <summary>
    /// Builds the retry policy. Internal and backoff-injectable so a test can exercise the disposal
    /// contract below without waiting out the real delays.
    /// </summary>
    /// <remarks>
    /// <c>onRetry</c> disposes the retried attempt's response. Polly hands the caller only the FINAL
    /// outcome, so without this every intermediate 5xx/408/429 response leaks its content buffer and
    /// keeps its connection out of the handler pool until finalization: exactly under the sustained
    /// backend failure the retries exist to survive. A retried <see cref="HttpRequestException"/>
    /// carries no result, hence the null-conditional. The final response is NOT disposed here, since
    /// the caller owns it.
    /// </remarks>
    internal static AsyncRetryPolicy<HttpResponseMessage> BuildRetryPolicy(Func<int, TimeSpan> backoff) => Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(IsRetryableResponse)
        .WaitAndRetryAsync(3, backoff, onRetry: (outcome, _) => outcome.Result?.Dispose());
}
