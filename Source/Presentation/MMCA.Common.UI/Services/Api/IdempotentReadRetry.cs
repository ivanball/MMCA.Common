using Polly.Retry;

namespace MMCA.Common.UI.Services.Api;

/// <summary>
/// The retry <see cref="AuthenticatedServiceBase"/> applies, reachable without inheriting from it, for
/// services that make anonymous reads (lookup services that create a plain <c>APIClient</c>) and would
/// otherwise get a single attempt.
/// <para>
/// <b>Idempotent reads only.</b> The policy re-sends the request on a transient failure (an
/// <see cref="HttpRequestException"/>, a 5xx other than 501/505, 408 or 429) up to three times with
/// exponential backoff (2s, 4s, 8s) plus jitter. Re-sending is only safe when doing the operation
/// twice is the same as doing it once, which is why the only entry point is a GET. A write that needs
/// retries goes through <see cref="EntityServiceBase{TEntityDTO, TIdentifierType}"/>, whose writes
/// carry an <c>Idempotency-Key</c> the server deduplicates on.
/// </para>
/// </summary>
public static class IdempotentReadRetry
{
    /// <summary>
    /// The policy instance itself: the same object <see cref="AuthenticatedServiceBase"/> exposes to
    /// its subclasses, so the two paths cannot drift.
    /// </summary>
    internal static AsyncRetryPolicy<HttpResponseMessage> Policy => AuthenticatedServiceBase.SharedRetryPolicy;

    /// <summary>
    /// Sends a GET through the shared retry policy. Every retried response is disposed by the policy;
    /// the caller owns the returned (final) one and must dispose it.
    /// </summary>
    /// <param name="httpClient">The client to send with (typically the named <c>APIClient</c>).</param>
    /// <param name="requestUri">The request URI, relative to the client's base address or absolute.</param>
    /// <param name="cancellationToken">Cancellation token, honoured between attempts and by each send.</param>
    /// <returns>The final response, retryable or not, once the policy stops.</returns>
    /// <exception cref="HttpRequestException">Every attempt failed without a response.</exception>
    public static Task<HttpResponseMessage> GetAsync(
        HttpClient httpClient,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(requestUri);

        return Policy.ExecuteAsync(token => httpClient.GetAsync(requestUri, token), cancellationToken);
    }
}
