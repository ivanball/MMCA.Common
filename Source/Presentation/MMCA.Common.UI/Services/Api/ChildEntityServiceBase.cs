using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Api;

/// <summary>
/// Base HTTP service for join/child entities that support POST (add) and DELETE (remove) but no
/// standalone reads: the many-to-many sibling of <see cref="EntityServiceBase{TEntityDTO, TId}"/>.
/// Uses the named <c>"APIClient"</c> HTTP client with the JWT Bearer token applied via
/// <see cref="AuthenticatedServiceBase.CreateAuthenticatedClientAsync"/> (join endpoints sit behind
/// <c>[Authorize]</c> like their parent CRUD endpoints) and returns every outcome as a
/// <see cref="Result"/>, with the API's own <see cref="ErrorType"/> preserved by
/// <see cref="ProblemDetailsResultReader"/>. Module-specific services derive from this, supply their
/// relative endpoint, and add typed <c>AddAsync</c>/<c>DeleteAsync</c> wrappers over
/// <see cref="PostAsync{TResponse}"/> / <see cref="DeleteByIdAsync"/>.
/// </summary>
public abstract class ChildEntityServiceBase(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    string endpoint) : AuthenticatedServiceBase(httpClientFactory, tokenStorageService)
{
    /// <summary>
    /// POSTs the join-entity payload and reads the created entity back from the response body.
    /// </summary>
    /// <typeparam name="TResponse">The DTO the endpoint answers with.</typeparam>
    /// <param name="request">
    /// The payload to post, typically an anonymous object. Declared as <see cref="object"/> on
    /// purpose: only the response type is named at the call site, so an anonymous payload can still
    /// be posted (a generic request parameter would force the caller to name a type it cannot spell).
    /// <see cref="System.Text.Json"/> serializes the runtime type for an <see cref="object"/> declaration.
    /// </param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <returns>The created DTO, or the errors the API described.</returns>
    protected async Task<Result<TResponse>> PostAsync<TResponse>(object request, CancellationToken cancellationToken) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(new Uri(endpoint, UriKind.Relative), request, cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync<TResponse>(response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <summary>
    /// POSTs the join-entity payload without reading a body back, for an endpoint that answers 204.
    /// </summary>
    /// <param name="request">The payload to post, typically an anonymous object.</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <returns>Success for any 2xx, otherwise the errors the API described.</returns>
    protected async Task<Result> PostAsync(object request, CancellationToken cancellationToken) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(new Uri(endpoint, UriKind.Relative), request, cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);

    /// <summary>
    /// DELETEs the join entity by id. A join row that is not there answers 404, which arrives as an
    /// <see cref="ErrorType.NotFound"/> failure rather than the old <see langword="false"/>, so the
    /// caller can still tell "nothing to remove" apart from "the remove failed".
    /// </summary>
    /// <param name="id">The join entity's identifier, already formatted for the route.</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <returns>Success for any 2xx, otherwise the errors the API described.</returns>
    protected async Task<Result> DeleteByIdAsync(string id, CancellationToken cancellationToken) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                var url = $"{endpoint}/{id}";
                using var response = await httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
}
