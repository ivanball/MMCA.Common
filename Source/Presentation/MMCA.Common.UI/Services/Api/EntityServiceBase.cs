using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Caching;

namespace MMCA.Common.UI.Services.Api;

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
/// <param name="endpoint">The service's relative endpoint, e.g. <c>products</c>.</param>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c> HttpClient.</param>
/// <param name="tokenStorageService">Circuit-scoped store the bearer token is read from.</param>
/// <param name="readCache">
/// Optional per-circuit read cache (ADR-040's client half). Supplied, the four read methods become
/// read-through with the TTL policy in <c>UiReadCacheOptions</c>, and every successful write
/// invalidates this endpoint's entries. Left <see langword="null"/>, every read goes to the API and
/// the class behaves exactly as it did before the cache existed.
/// </param>
public abstract class EntityServiceBase<TEntityDTO, TIdentifierType>(
    string endpoint,
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    IUiReadCache? readCache = null) : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IEntityService<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    protected string Endpoint { get; } = endpoint;

    /// <summary>
    /// The read cache this service reads through, or <see langword="null"/> when the host registered
    /// none. A derived service reaches for it directly only to invalidate a route prefix its own
    /// custom write touches; the CRUD verbs below already invalidate <see cref="Endpoint"/>.
    /// </summary>
    protected IUiReadCache? ReadCache { get; } = readCache;

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
        var wrapper = await GetCachedAsync<PagedCollectionResult<TEntityDTO>>(url, cancellationToken);

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
        var result = await GetCachedAsync<PagedCollectionResult<TEntityDTO>>(url, cancellationToken);

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
        var result = await GetCachedAsync<CollectionResult<BaseLookup<TIdentifierType>>>(url, cancellationToken);

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
        // as the same null. A failure is never cached, so a 404 is re-asked every time.
        return await GetCachedAsync<TEntityDTO>(url, cancellationToken);
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
        var result = await SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.PostAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            idempotencyKey: NewIdempotencyKey()
        );

        InvalidateOnSuccess(result);
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The update is conditional (ADR-035): the DTO's concurrency token is sent as the
    /// <c>If-Match</c> header, which is the only route the token travels. A DTO that carries no token
    /// sends no header, and the server refuses the write with <c>428 Precondition Required</c> rather
    /// than overwriting another editor's change.
    /// </remarks>
    public virtual async Task<Result> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{GetEntityId(entity)}";
        var result = await SendRequestAsync(
            httpClient => httpClient.PutAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            ifMatch: ConcurrencyTagOf(entity)
        );

        InvalidateOnSuccess(result);
        return result;
    }

    /// <summary>
    /// The <c>If-Match</c> value for a DTO: its concurrency token rendered as a weak entity tag, or
    /// null when this DTO type carries no token.
    /// </summary>
    /// <param name="entity">The DTO about to be written back.</param>
    /// <returns>The entity tag, or <see langword="null"/>.</returns>
    protected static string? ConcurrencyTagOf(TEntityDTO entity) =>
        entity is IConcurrencyAware { RowVersion: { Length: > 0 } rowVersion }
            ? ConcurrencyETag.Format(rowVersion)
            : null;

    /// <inheritdoc />
    public virtual async Task<Result> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{id}";
        var result = await SendRequestAsync(
            httpClient => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );

        InvalidateOnSuccess(result);
        return result;
    }

    protected virtual TIdentifierType GetEntityId(TEntityDTO entity)
        => entity.Id;

    /// <summary>
    /// A read that goes through <see cref="ReadCache"/> when the host registered one: a fresh entry
    /// answers without a round trip, a miss fetches and stores, and a failure is returned without
    /// being stored. With no cache registered (or with <paramref name="bypassCache"/> set) this is
    /// exactly the plain GET the read methods used to issue.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response body into, and the cached type.</typeparam>
    /// <param name="url">The relative URL, path plus the full query string. It IS the cache key, so
    /// two reads that differ by a single query parameter are two entries, matching the server-side
    /// output cache's <c>QueryKeys = "*"</c> rule (ADR-040).</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <param name="bypassCache">
    /// Forces a round trip and leaves the cache untouched. For a read the user explicitly asked to be
    /// current (a refresh button, a re-poll after a push), where serving a fresh-by-the-clock entry
    /// would answer a question the user did not ask.
    /// </param>
    /// <returns>The value, from cache or from the API, or the failure the API described.</returns>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "The string IS the cache key as well as the request path: it must be stored and compared verbatim so it matches the server-side output-cache key shape (path + full query, ADR-040), and a System.Uri round trip would re-encode it. The read methods above already build this exact string, and the sibling SendRequestAsync overloads take the same shape.")]
    protected async Task<Result<T>> GetCachedAsync<T>(
        string url,
        CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (ReadCache is null || bypassCache)
        {
            return await SendGetAsync<T>(url, cancellationToken);
        }

        if (ReadCache.TryGetFresh<T>(url, out var cached) && cached is not null)
        {
            return Result.Success(cached);
        }

        var result = await SendGetAsync<T>(url, cancellationToken);

        // Only a success is stored: caching a failure would pin a transient outage in front of the
        // user for the whole TTL, and a 404 would survive the create that fixed it.
        if (result is { IsSuccess: true, Value: not null })
        {
            ReadCache.Set(url, result.Value);
        }

        return result;
    }

    private Task<Result<T>> SendGetAsync<T>(string url, CancellationToken cancellationToken) =>
        SendRequestAsync<T>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken);

    /// <summary>
    /// Drops this endpoint's cached reads after a write actually succeeded. Only on success: a
    /// rejected write changed nothing, and invalidating there would throw away entries that are
    /// still accurate.
    /// </summary>
    /// <param name="result">The write's outcome.</param>
    private void InvalidateOnSuccess(Result result)
    {
        if (result.IsSuccess)
        {
            ReadCache?.InvalidatePrefix(Endpoint);
        }
    }

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
    /// <param name="ifMatch">
    /// Optional entity tag sent as the <c>If-Match</c> header, stating the version the write is
    /// conditional on (see <see cref="ConcurrencyETag"/>). Required by every endpoint the framework
    /// guards with <c>[SupportsIfMatch]</c>.
    /// </param>
    /// <returns>
    /// The deserialized value, or a failure. A 2xx with no body fails with
    /// <see cref="ProblemDetailsResultReader.EmptyResponseCode"/>: use the non-generic overload for
    /// endpoints that legitimately answer without one.
    /// </returns>
    protected async Task<Result<T>> SendRequestAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                // The client stays alive across the read: HttpClient buffers the body before the send
                // task completes, but keeping it in scope means that is a property, not a dependency.
                using var httpClient = await CreateRequestClientAsync(idempotencyKey, ifMatch);
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
    /// <param name="ifMatch">Optional <c>If-Match</c> entity tag stating the version the write is conditional on.</param>
    /// <returns>Success for any 2xx, otherwise the errors the response described.</returns>
    protected async Task<Result> SendRequestAsync(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateRequestClientAsync(idempotencyKey, ifMatch);
                using var response = await RetryPolicy.ExecuteAsync(_ => httpAction(httpClient), cancellationToken);
                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Builds the authenticated client for one logical operation, carrying the idempotency key and
    /// the conditional-write precondition when the operation needs them.
    /// </summary>
    private async Task<HttpClient> CreateRequestClientAsync(string? idempotencyKey, string? ifMatch)
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

        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            // Same reasoning as the idempotency key: every retry states the same precondition, so a
            // write that lost the race fails the precondition on each attempt instead of succeeding
            // on a later one against a version the caller never saw.
            httpClient.DefaultRequestHeaders.Add(ConcurrencyETag.IfMatchHeaderName, ifMatch);
        }

        return httpClient;
    }
}
