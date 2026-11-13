using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Tests.Fakes;

/// <summary>An entity used only by the cursor-paging and change-feed controller tests.</summary>
public sealed class PagingEntity : AuditableBaseEntity<int>;

/// <summary>Its DTO.</summary>
public sealed record PagingDTO : IBaseDTO<int>
{
    /// <inheritdoc />
    public required int Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? Name { get; init; }
}

/// <summary>One recorded <c>GetPageByCursorAsync</c> call.</summary>
/// <param name="Request">The keyset request the controller built.</param>
/// <param name="Specification">The specification the controller passed.</param>
/// <param name="CancellationToken">The token the controller passed.</param>
public sealed record CursorPageCall(
    KeysetPageRequest Request,
    ISpecification<PagingEntity, int>? Specification,
    CancellationToken CancellationToken);

/// <summary>One recorded <c>GetChangesSinceAsync</c> call.</summary>
/// <param name="Since">The lower bound the controller passed.</param>
/// <param name="Cursor">The cursor the controller passed.</param>
/// <param name="PageSize">The page size the controller passed.</param>
/// <param name="Specification">The specification the controller passed.</param>
/// <param name="CancellationToken">The token the controller passed.</param>
public sealed record ChangeFeedCall(
    DateTime? Since,
    string? Cursor,
    int PageSize,
    ISpecification<PagingEntity, int>? Specification,
    CancellationToken CancellationToken);

/// <summary>
/// Query-service stand-in that implements the two keyset reads (cursor page and change feed),
/// records every call, and answers with a configurable result. Every other member is unused by
/// the tests that take it and throws.
/// </summary>
public sealed class PagingQueryService : IEntityQueryService<PagingEntity, PagingDTO, int>
{
    /// <summary>Gets the recorded cursor-page calls, in call order.</summary>
    public List<CursorPageCall> CursorCalls { get; } = [];

    /// <summary>Gets the recorded change-feed calls, in call order.</summary>
    public List<ChangeFeedCall> ChangeFeedCalls { get; } = [];

    /// <summary>Gets or sets the answer to <c>GetPageByCursorAsync</c>.</summary>
    public Result<KeysetCollectionResult<PagingDTO>> CursorResult { get; set; } =
        Result.Success(new KeysetCollectionResult<PagingDTO>([], nextCursor: null));

    /// <summary>Gets or sets the answer to <c>GetChangesSinceAsync</c>.</summary>
    public Result<KeysetCollectionResult<EntityChange<PagingDTO, int>>> ChangeFeedResult { get; set; } =
        Result.Success(new KeysetCollectionResult<EntityChange<PagingDTO, int>>([], nextCursor: null));

    /// <inheritdoc />
    public IEntityDTOMapper<PagingEntity, PagingDTO, int> DTOMapper => throw new NotSupportedException();

    /// <summary>Records the call and returns <see cref="CursorResult"/>.</summary>
    /// <param name="request">The keyset request.</param>
    /// <param name="specification">The read scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configured result.</returns>
    public Task<Result<KeysetCollectionResult<PagingDTO>>> GetPageByCursorAsync(
        KeysetPageRequest request,
        ISpecification<PagingEntity, int>? specification = null,
        CancellationToken cancellationToken = default)
    {
        CursorCalls.Add(new CursorPageCall(request, specification, cancellationToken));
        return Task.FromResult(CursorResult);
    }

    /// <summary>Records the call and returns <see cref="ChangeFeedResult"/>.</summary>
    /// <param name="since">The inclusive lower bound.</param>
    /// <param name="cursor">The opaque cursor.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="specification">The read scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configured result.</returns>
    public Task<Result<KeysetCollectionResult<EntityChange<PagingDTO, int>>>> GetChangesSinceAsync(
        DateTime? since,
        string? cursor,
        int pageSize,
        ISpecification<PagingEntity, int>? specification = null,
        CancellationToken cancellationToken = default)
    {
        ChangeFeedCalls.Add(new ChangeFeedCall(since, cursor, pageSize, specification, cancellationToken));
        return Task.FromResult(ChangeFeedResult);
    }

    /// <inheritdoc />
    public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<PagingEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<PagingEntity, int>? specification = null,
        Dictionary<string, (string Operator, string Value)>? filters = null,
        string? sortColumn = null,
        string? sortDirection = null,
        string? fields = null,
        int? pageNumber = null,
        int? pageSize = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<IReadOnlyCollection<BaseLookup<int>>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<PagingEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<PagingEntity>> GetEntityByIdAsync(
        string idValue,
        string? idField = null,
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<PagingEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<object>> GetByIdAsync(
        int id,
        bool includeFKs = false,
        bool includeChildren = false,
        ISpecification<PagingEntity, int>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Expression<Func<PagingEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
