using System.Net;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Base HTTP service implementing <see cref="IEntityService{TEntityDTO, TIdentifierType}"/>.
/// Provides CRUD operations over the WebAPI with:
/// <list type="bullet">
///   <item>Polly exponential-backoff retry (3 attempts) for transient/server errors</item>
///   <item>Automatic domain exception extraction via <see cref="ServiceExceptionHelper"/></item>
///   <item>Named <c>"APIClient"</c> HttpClient with pre-configured base address and auth handler</item>
/// </list>
/// Module-specific services derive from this and add domain-specific operations.
/// </summary>
/// <typeparam name="TEntityDTO">DTO type returned by the API.</typeparam>
/// <typeparam name="TIdentifierType">Primary key type of the entity.</typeparam>
public abstract class EntityServiceBase<TEntityDTO, TIdentifierType>(
    string endpoint,
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IEntityService<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    protected string Endpoint { get; } = endpoint;

    public virtual async Task<IReadOnlyList<TEntityDTO>?> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>
        {
            $"includeFKs={includeFKs}",
            $"includeChildren={includeChildren}"
        };

        var url = $"{Endpoint}?{string.Join("&", queryParams)}";
        var wrapper = await SendRequestAsync<PagedCollectionResult<TEntityDTO>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
        return (IReadOnlyList<TEntityDTO>)(wrapper?.Items ?? []);
    }

    public virtual async Task<(IReadOnlyList<TEntityDTO> Items, int TotalItems)> GetPagedAsync(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>
        {
            $"pageNumber={pageNumber}",
            $"pageSize={pageSize}",
            $"sortColumn={Uri.EscapeDataString(sortColumn ?? string.Empty)}",
            $"sortDirection={Uri.EscapeDataString(sortDirection ?? string.Empty)}",
            $"includeChildren={includeChildren}"
        };

        if (filters is not null)
        {
            foreach (var (property, (op, value)) in filters)
            {
                if (!string.IsNullOrWhiteSpace(op))
                {
                    queryParams.Add($"filters[{Uri.EscapeDataString(property)}].operator={Uri.EscapeDataString(op)}");
                    if (!string.IsNullOrWhiteSpace(value))
                        queryParams.Add($"filters[{Uri.EscapeDataString(property)}].value={Uri.EscapeDataString(value)}");
                }
            }
        }

        var url = $"{Endpoint}/paged?{string.Join("&", queryParams)}";
        var result = await SendRequestAsync<PagedCollectionResult<TEntityDTO>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
        return ((IReadOnlyList<TEntityDTO>)(result?.Items ?? []), result?.PaginationMetadata.TotalItemCount ?? 0);
    }

    public virtual async Task<IReadOnlyList<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/lookup?nameProperty={nameProperty}";
        var result = await SendRequestAsync<CollectionResult<BaseLookup<TIdentifierType>>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
        return (IReadOnlyList<BaseLookup<TIdentifierType>>)(result?.Items ?? []);
    }

    public virtual async Task<TEntityDTO?> GetByIdAsync(
        TIdentifierType id,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>
        {
            $"includeChildren={includeChildren}"
        };

        var url = $"{Endpoint}/{id}?{string.Join("&", queryParams)}";
        return await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken,
            treatNotFoundAsDefault: true
        );
    }

    public virtual async Task<TEntityDTO> AddAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}";
        return await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.PostAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            throwIfNull: true
        ) ?? throw new InvalidOperationException($"No {typeof(TEntityDTO).Name} returned from API.");
    }

    public virtual async Task<bool> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{GetEntityId(entity)}";
        await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.PutAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            expectContent: false
        );
        return true;
    }

    public virtual async Task<bool> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{id}";
        await SendRequestAsync<object>(
            httpClient => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken,
            expectContent: false
        );
        return true;
    }

    protected virtual TIdentifierType GetEntityId(TEntityDTO entity)
        => entity.Id;

    /// <summary>
    /// Central HTTP dispatch: executes the given action through the Polly retry pipeline,
    /// checks for domain-level errors in the response body, and deserializes the result.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response body into.</typeparam>
    /// <param name="httpAction">Lambda that performs the actual HTTP call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="treatNotFoundAsDefault">When <see langword="true"/>, 404 returns default instead of throwing.</param>
    /// <param name="throwIfNull">When <see langword="true"/>, throws if deserialized result is <see langword="null"/>.</param>
    /// <param name="expectContent">When <see langword="false"/>, skips deserialization (PUT/DELETE with no body).</param>
    protected async Task<T?> SendRequestAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        bool treatNotFoundAsDefault = false,
        bool throwIfNull = false,
        bool expectContent = true)
    {
        using var httpClient = await CreateAuthenticatedClientAsync();

        var response = await RetryPolicy.ExecuteAsync(() => httpAction(httpClient));

        if (treatNotFoundAsDefault && response.StatusCode == HttpStatusCode.NotFound)
            return default;

        // Extract domain/validation errors before EnsureSuccessStatusCode throws a generic exception
        if (!response.IsSuccessStatusCode)
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);

        response.EnsureSuccessStatusCode();

        if (!expectContent)
            return default;

        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

        if (throwIfNull && result is null)
            throw new InvalidOperationException($"No {typeof(T).Name} returned from API.");

        return result;
    }

}
