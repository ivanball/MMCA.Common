using System.Net;
using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Base HTTP service for join/child entities that support POST (add) and DELETE (remove) but no
/// standalone reads — the many-to-many sibling of <see cref="EntityServiceBase{TEntityDTO, TId}"/>.
/// Uses the named <c>"APIClient"</c> HTTP client with the JWT Bearer token applied via
/// <see cref="AuthenticatedServiceBase.CreateAuthenticatedClientAsync"/> (join endpoints sit behind
/// <c>[Authorize]</c> like their parent CRUD endpoints) and extracts domain errors via
/// <see cref="ServiceExceptionHelper"/>. Module-specific services derive from this, supply their
/// relative endpoint, and add typed <c>AddAsync</c>/<c>DeleteAsync</c> wrappers over
/// <see cref="PostAsync"/> / <see cref="DeleteByIdAsync"/>.
/// </summary>
public abstract class ChildEntityServiceBase(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    string endpoint) : AuthenticatedServiceBase(httpClientFactory, tokenStorageService)
{
    /// <summary>POSTs the join-entity payload to the endpoint, surfacing domain errors.</summary>
    /// <typeparam name="TRequest">The join-entity request payload type (typically an anonymous object).</typeparam>
    protected async Task<HttpResponseMessage> PostAsync<TRequest>(TRequest request, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateAuthenticatedClientAsync();
        var response = await httpClient.PostAsJsonAsync(new Uri(endpoint, UriKind.Relative), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>DELETEs the join entity by id; <see langword="false"/> when it was not found.</summary>
    protected async Task<bool> DeleteByIdAsync(string id, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{endpoint}/{id}";
        var response = await httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
