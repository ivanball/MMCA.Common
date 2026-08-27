using System.Globalization;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Base HTTP service implementing <see cref="IEntityService{TEntityDTO, TIdentifierType}"/>.
/// Provides CRUD operations over the WebAPI with:
/// <list type="bullet">
///   <item>Polly exponential-backoff-with-jitter retry (3 retries) for transient/server errors</item>
///   <item>An <c>Idempotency-Key</c> on creates, held constant across every retry attempt</item>
///   <item>Every outcome returned as a <see cref="Result"/>: the API's own errors are read back
///   through <see cref="ProblemDetailsResultReader"/> with their <see cref="ErrorType"/> intact,
///   and transport faults become failures via <see cref="HttpResultExecutor"/></item>
///   <item>Named <c>"APIClient"</c> HttpClient with pre-configured base address and auth handler</item>
/// </list>
/// Module-specific services derive from this and add domain-specific operations.
/// <para>
/// <b>Nothing here throws for a server answer.</b> A 404 is a <see cref="ErrorType.NotFound"/>
/// failure, a validation rejection is a <see cref="ErrorType.Validation"/> failure, a 500 is
/// <see cref="ErrorType.Unexpected"/>. The only exception that still escapes is the caller's own
/// <see cref="OperationCanceledException"/>, because a page owns its cancellation.
/// </para>
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

    /// <inheritdoc />
    public virtual async Task<Result<IReadOnlyList<TEntityDTO>>> GetAllAsync(
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

        return wrapper.Map(page => AsReadOnlyList(page.Items));
    }

    /// <inheritdoc />
    public virtual async Task<Result<(IReadOnlyList<TEntityDTO> Items, int TotalItems)>> GetPagedAsync(
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
            string.Create(CultureInfo.InvariantCulture, $"pageNumber={pageNumber}"),
            string.Create(CultureInfo.InvariantCulture, $"pageSize={pageSize}"),
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

        // The pagination metadata still travels with the page; only its shape is flattened, so a
        // grid keeps binding to (Items, TotalItems) exactly as before.
        return result.Map<(IReadOnlyList<TEntityDTO> Items, int TotalItems)>(
            page => (AsReadOnlyList(page.Items), page.PaginationMetadata.TotalItemCount));
    }

    /// <inheritdoc />
    public virtual async Task<Result<IReadOnlyList<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/lookup?nameProperty={Uri.EscapeDataString(nameProperty)}";
        var result = await SendRequestAsync<CollectionResult<BaseLookup<TIdentifierType>>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );

        return result.Map(collection => AsReadOnlyList(collection.Items));
    }

    /// <inheritdoc />
    public virtual async Task<Result<TEntityDTO>> GetByIdAsync(
        TIdentifierType id,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>
        {
            $"includeChildren={includeChildren}"
        };

        var url = $"{Endpoint}/{id}?{string.Join("&", queryParams)}";

        // A missing entity is a NotFound failure rather than a null value: the caller can tell it
        // apart from a transport failure (ResultUiExtensions.IsNotFound) instead of both arriving
        // as the same null.
        return await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public virtual async Task<Result<TEntityDTO>> AddAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}";

        // Creates are the one CRUD verb that is not naturally idempotent: a retried POST whose
        // first attempt actually reached the server would create a second record. The key makes the
        // server collapse the duplicate; reads, updates (full PUT) and deletes need no key.
        return await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.PostAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            idempotencyKey: NewIdempotencyKey()
        );
    }

    /// <inheritdoc />
    public virtual async Task<Result> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{GetEntityId(entity)}";
        return await SendRequestAsync(
            httpClient => httpClient.PutAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public virtual async Task<Result> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{id}";
        return await SendRequestAsync(
            httpClient => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
    }

    protected virtual TIdentifierType GetEntityId(TEntityDTO entity)
        => entity.Id;

    /// <summary>
    /// Presents a deserialized <c>Items</c> collection as an <see cref="IReadOnlyList{T}"/> without
    /// assuming the JSON reader produced a list: the common case casts, anything else is copied.
    /// </summary>
    private static IReadOnlyList<TItem> AsReadOnlyList<TItem>(ICollection<TItem>? items) =>
        items switch
        {
            null => [],
            IReadOnlyList<TItem> list => list,
            _ => [.. items],
        };

    /// <summary>
    /// Central HTTP dispatch for a call that returns a body: executes the action through the Polly
    /// retry pipeline, then reads the response back into a <see cref="Result{T}"/> through
    /// <see cref="ProblemDetailsResultReader"/>.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response body into.</typeparam>
    /// <param name="httpAction">Lambda that performs the actual HTTP call.</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <param name="idempotencyKey">
    /// Optional idempotency key sent as the <c>Idempotency-Key</c> header (see
    /// <see cref="AuthenticatedServiceBase.NewIdempotencyKey"/>). Supply one for non-idempotent
    /// writes (creates); leave <see langword="null"/> for reads and naturally idempotent writes.
    /// </param>
    /// <returns>
    /// The deserialized value, or a failure. A 2xx with no body fails with
    /// <see cref="ProblemDetailsResultReader.EmptyResponseCode"/>: use the non-generic overload for
    /// endpoints that legitimately answer without one.
    /// </returns>
    protected async Task<Result<T>> SendRequestAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                // The client stays alive across the read: HttpClient buffers the body before the send
                // task completes, but keeping it in scope means that is a property, not a dependency.
                using var httpClient = await CreateRequestClientAsync(idempotencyKey);
                using var response = await RetryPolicy.ExecuteAsync(_ => httpAction(httpClient), cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync<T>(response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Central HTTP dispatch for a call with no response body (a PUT or DELETE answering 204):
    /// executes the action through the Polly retry pipeline, then classifies the response through
    /// <see cref="ProblemDetailsResultReader"/>.
    /// </summary>
    /// <param name="httpAction">Lambda that performs the actual HTTP call.</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <param name="idempotencyKey">Optional <c>Idempotency-Key</c> header value.</param>
    /// <returns>Success for any 2xx, otherwise the errors the response described.</returns>
    protected async Task<Result> SendRequestAsync(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateRequestClientAsync(idempotencyKey);
                using var response = await RetryPolicy.ExecuteAsync(_ => httpAction(httpClient), cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Builds the authenticated client for one logical operation, carrying the idempotency key when
    /// the operation needs one.
    /// </summary>
    private async Task<HttpClient> CreateRequestClientAsync(string? idempotencyKey)
    {
        var httpClient = await CreateAuthenticatedClientAsync();

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // This client is created once per logical operation and then serves EVERY retry attempt
            // made by the policy, so a default header set here rides along on each attempt with the
            // same value. That is exactly the property the server needs: the same key across
            // attempts means duplicate arrivals dedup instead of creating extra records.
            httpClient.DefaultRequestHeaders.Add(IdempotencyHeaders.IdempotencyKey, idempotencyKey);
        }

        return httpClient;
    }
}
