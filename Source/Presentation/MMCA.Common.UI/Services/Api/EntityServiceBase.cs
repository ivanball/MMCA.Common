using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Api;

/// <summary>
/// Base HTTP service implementing <see cref="IEntityService{TEntityDTO, TIdentifierType}"/>.
/// Provides CRUD operations over the WebAPI with:
/// <list type="bullet">
///   <item>Polly exponential-backoff-with-jitter retry (3 retries) for transient/server errors</item>
///   <item>An <c>Idempotency-Key</c> on creates and updates, held constant across every retry attempt</item>
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
public abstract class EntityServiceBase<TEntityDTO, TIdentifierType>(
    string endpoint,
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IEntityService<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    protected string Endpoint { get; } = endpoint;

    /// <summary>
    /// Gets a value indicating whether <see cref="GetPagedAsync"/> asks the API to include foreign-key
    /// navigations (<c>includeFKs=True</c>). Off by default, so the paged URL is unchanged; a service
    /// whose list grid shows a foreign-key name (a product's category) overrides it to <see langword="true"/>.
    /// </summary>
    protected virtual bool PagedIncludeFKs => false;

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
        var wrapper = await SendGetAsync<PagedCollectionResult<TEntityDTO>>(url, cancellationToken);

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

        if (PagedIncludeFKs)
        {
            queryParams.Add("includeFKs=True");
        }

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
        var result = await SendGetAsync<PagedCollectionResult<TEntityDTO>>(url, cancellationToken);

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
        var result = await SendGetAsync<CollectionResult<BaseLookup<TIdentifierType>>>(url, cancellationToken);

        return result.Map(collection => AsReadOnlyList(collection.Items));
    }

    /// <inheritdoc />
    public virtual Task<Result<TEntityDTO>> GetByIdAsync(
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
        return SendGetAsync<TEntityDTO>(url, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<Result<TEntityDTO>> AddAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}";

        // A retried POST whose first attempt actually reached the server would create a second
        // record. The key makes the server collapse the duplicate into one execution plus a replayed
        // response.
        return SendRequestAsync<TEntityDTO>(
            httpClient => httpClient.PostAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            idempotencyKey: NewIdempotencyKey()
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// The update is conditional (ADR-035): the DTO's concurrency token is sent as the
    /// <c>If-Match</c> header, which is the only route the token travels. A DTO that carries no token
    /// sends no header, and the server refuses the write with <c>428 Precondition Required</c> rather
    /// than overwriting another editor's change.
    /// </remarks>
    public virtual Task<Result> UpdateAsync(
        TEntityDTO entity,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{GetEntityId(entity)}";

        // The key is minted once, outside the retry, so every attempt carries the same one. A PUT
        // whose first attempt landed and only lost its response is then replayed by the server's
        // idempotency filter (the stored 204) instead of being re-run against the version it just
        // replaced, which would answer 412 and tell the user someone else changed the item.
        return SendRequestAsync(
            httpClient => httpClient.PutAsJsonAsync(new Uri(url, UriKind.Relative), entity, cancellationToken),
            cancellationToken,
            idempotencyKey: NewIdempotencyKey(),
            ifMatch: ConcurrencyTagOf(entity)
        );
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
    public virtual Task<Result> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{id}";
        return SendRequestAsync(
            httpClient => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken
        );
    }

    protected virtual TIdentifierType GetEntityId(TEntityDTO entity)
        => entity.Id;

    /// <summary>
    /// A GET through the retry pipeline, read back into a <see cref="Result{T}"/>: the read the CRUD
    /// methods above issue, exposed so a derived service's own read (a storefront page, a filtered
    /// lookup) takes the same path.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response body into.</typeparam>
    /// <param name="url">The relative URL, path plus the full query string.</param>
    /// <param name="cancellationToken">Cancellation token; caller cancellation still propagates as an exception.</param>
    /// <returns>The value, or the failure the API described.</returns>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "The read methods above already build this exact relative string (path plus escaped query), and the sibling SendRequestAsync overloads take the same shape; a System.Uri round trip would only re-encode it.")]
    protected Task<Result<T>> SendGetAsync<T>(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        return SendRequestAsync<T>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken);
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
    /// writes (creates, and updates so a replayed attempt is answered from the stored outcome); leave
    /// <see langword="null"/> for reads and naturally idempotent writes.
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
    protected Task<Result<T>> SendRequestAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return HttpResultExecutor.ExecuteAsync(
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
    protected Task<Result> SendRequestAsync(
        Func<HttpClient, Task<HttpResponseMessage>> httpAction,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        ArgumentNullException.ThrowIfNull(httpAction);

        return HttpResultExecutor.ExecuteAsync(
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
            // on a later one against a version the caller never saw. When the first attempt landed
            // and only its response was lost, the idempotency key UpdateAsync sends beside this header
            // lets the server replay the stored outcome instead of re-checking the precondition
            // against the version that attempt just wrote.
            httpClient.DefaultRequestHeaders.Add(ConcurrencyETag.IfMatchHeaderName, ifMatch);
        }

        return httpClient;
    }
}
