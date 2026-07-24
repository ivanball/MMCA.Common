using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Generic query service that provides filtered, sorted, paginated, and field-projected
/// reads for any entity. Orchestrates a multi-step pipeline:
/// <list type="number">
///   <item>Validate incoming parameters (fields, sort, filters)</item>
///   <item>Build navigation metadata (supported vs unsupported includes)</item>
///   <item>Execute the query pipeline (include, filter, sort, paginate, project)</item>
///   <item>Map entities to DTOs and shape output fields</item>
/// </list>
/// Subclasses can override <see cref="DTOToEntityPropertyMap"/> to support filtering/sorting
/// on DTO property names that differ from entity property names.
/// </summary>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type returned to consumers.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public class EntityQueryService<TEntity, TEntityDTO, TIdentifierType>(
    IUnitOfWork unitOfWork,
    INavigationMetadataProvider navigationMetadataProvider,
    IEntityQueryPipeline queryPipeline,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper,
    INavigationPopulator<TEntity> navigationPopulator)
    : IEntityQueryService<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Gets the unit of work for subclass use (e.g. custom queries).</summary>
    protected IUnitOfWork UnitOfWork { get; } = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private INavigationMetadataProvider NavigationMetadataProvider { get; } = navigationMetadataProvider ?? throw new ArgumentNullException(nameof(navigationMetadataProvider));
    private IEntityQueryPipeline QueryPipeline { get; } = queryPipeline ?? throw new ArgumentNullException(nameof(queryPipeline));

    /// <summary>Gets the read repository. Override to provide a custom repository (e.g. with query filters).</summary>
    protected virtual IReadRepository<TEntity, TIdentifierType> Repository { get; } = unitOfWork.GetReadRepository<TEntity, TIdentifierType>();

    /// <inheritdoc />
    public IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> DTOMapper { get; } = dtoMapper ?? throw new ArgumentNullException(nameof(dtoMapper));

    /// <summary>Gets the navigation populator for manually loading unsupported navigations.</summary>
    public INavigationPopulator<TEntity> NavigationPopulator { get; } = navigationPopulator ?? throw new ArgumentNullException(nameof(navigationPopulator));

    /// <summary>
    /// Maps DTO property names to entity property paths for filtering and sorting.
    /// Override in subclasses when DTO field names differ from entity properties
    /// (e.g. <c>"CategoryName" -> "Category.Name"</c>).
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> DTOToEntityPropertyMap { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private const string IdField = "Id";

    // Cached TypeConverter per identifier type: the by-id fast path parses the string id into the
    // typed key without per-call reflection. A type with no usable string converter simply misses
    // the fast path and uses the pipeline.
    private static readonly ConcurrentDictionary<Type, TypeConverter?> IdConverterCache = new();

    /// <summary>
    /// Attempts the keyed by-id fast path: for a plain primary-key lookup (no field projection, no
    /// includes, no specification, default id field) it issues a single <c>TOP 1 WHERE Id = @id</c>
    /// via the repository's include overload, skipping the dynamic-filter pipeline (which parses a
    /// string predicate and emits <c>TOP 1000</c> + a client-side <c>FirstOrDefault</c>). That
    /// overload runs on the filtered <c>TableNoTracking</c>, so soft-delete query filters still
    /// apply (unlike <c>FindAsync</c>, which bypasses them). Returns <see langword="null"/> when the
    /// request is not a plain key lookup or the id is not convertible, so the caller uses the
    /// pipeline.
    /// </summary>
    private async Task<Result<TEntity>?> TryGetByIdFastPathAsync(
        string idValue,
        string? idField,
        bool includeFKs,
        bool includeChildren,
        Specification<TEntity, TIdentifierType>? specification,
        string? fields,
        bool asTracking,
        CancellationToken cancellationToken)
    {
        if (!IsPrimaryKeyOnlyLookup(idField, includeFKs, includeChildren, specification, fields)
            || !TryConvertId(idValue, out var typedId))
        {
            return null;
        }

        var entity = await Repository.GetByIdAsync(typedId, [], asTracking, cancellationToken).ConfigureAwait(false);
        return entity is null
            ? Result.Failure<TEntity>(Error.NotFound.WithSource(nameof(GetByIdAsync)).WithTarget(typeof(TEntity).Name))
            : Result.Success(entity);
    }

    /// <summary>
    /// Whether the request is a plain primary-key lookup the fast path can serve.
    /// </summary>
    /// <remarks>
    /// <paramref name="includeFKs"/> and <paramref name="includeChildren"/> only disqualify the
    /// request when the entity actually has navigations to include. Treating the flags themselves
    /// as disqualifying made the fast path unreachable from the REST layer, whose by-id action
    /// defaults <c>includeFKs</c> to true: every by-id read fell back to the dynamic-filter
    /// pipeline, which parses a string predicate and emits <c>TOP 1000</c> plus a client-side
    /// <c>FirstOrDefault</c> where a keyed <c>TOP 1 WHERE Id = @id</c> would do.
    /// </remarks>
    private bool IsPrimaryKeyOnlyLookup(
        string? idField,
        bool includeFKs,
        bool includeChildren,
        Specification<TEntity, TIdentifierType>? specification,
        string? fields)
    {
        if (fields is not null
            || specification is not null
            || idField is not null && !string.Equals(idField, IdField, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!includeFKs && !includeChildren)
        {
            return true;
        }

        // Asking for includes on an entity that declares none is the same request as asking for
        // none, so it stays on the fast path.
        var metadata = NavigationMetadataProvider.BuildIncludes<TEntity>(includeFKs, includeChildren);
        return metadata.SupportedIncludes.Count == 0 && metadata.UnsupportedIncludes.Count == 0;
    }

    /// <summary>
    /// Converts the string id to <typeparamref name="TIdentifierType"/> for the keyed fast path.
    /// Returns <see langword="false"/> (so the caller falls back to the pipeline) when the value is
    /// not convertible, preserving the pipeline's tolerance of malformed ids.
    /// </summary>
    private static bool TryConvertId(string idValue, out TIdentifierType id)
    {
        id = default!;
        var converter = IdConverterCache.GetOrAdd(typeof(TIdentifierType), static t =>
        {
            var c = TypeDescriptor.GetConverter(t);
            return c.CanConvertFrom(typeof(string)) ? c : null;
        });

        if (converter is null)
            return false;

        try
        {
            if (converter.ConvertFromString(null, CultureInfo.InvariantCulture, idValue) is TIdentifierType converted)
            {
                id = converted;
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            // Malformed id for this key type: fall back to the pipeline path.
        }

        return false;
    }

    /// <inheritdoc />
    public virtual async Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
        => await GetAllAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            filters: null,
            sortColumn: null,
            sortDirection: null,
            fields: fields,
            pageNumber: null,
            pageSize: null,
            asTracking: asTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async Task<Result<PagedCollectionResult<object>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        Dictionary<string, (string Operator, string Value)>? filters = null,
        string? sortColumn = null,
        string? sortDirection = null,
        string? fields = null,
        int? pageNumber = null,
        int? pageSize = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Validate all query parameters upfront before touching the database
        var validateResult = Result.Combine(
            QueryFieldService.Validate<TEntity>(fields, allowWriteableFields: false),
            QueryFieldService.Validate<TEntity>(sortColumn, allowWriteableFields: true),
            QueryFieldService.ValidateSortDirection(sortDirection),
            QueryFilterService.ValidateFilters<TEntity>(filters, DTOToEntityPropertyMap)
            );
        if (validateResult.IsFailure)
        {
            var errors = validateResult.Errors
                .Select(e => e with
                {
                    Source = nameof(GetAllAsync),
                    Target = typeof(TEntity).Name
                })
                .ToList();

            return Result.Failure<PagedCollectionResult<object>>(errors);
        }

        // Step 2: Execute the query pipeline (includes, criteria, filters, sort, pagination, field selection)
        var (entities, totalItemCount) = await BuildQueryAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            filters: filters,
            sortColumn: sortColumn,
            sortDirection: sortDirection,
            fields: fields,
            pageNumber: pageNumber,
            pageSize: pageSize,
            asTracking: asTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Step 3: Map entities to DTOs and shape output to requested fields.
        // Shaping into dynamic objects only pays off when a field subset was requested;
        // otherwise the typed DTOs are returned as-is and serialize to the same camelCase
        // JSON without the per-row ExpandoObject allocation and boxing.
        var paginationMetadata = BuildPaginationMetadata(totalItemCount, pageNumber, pageSize);

        var pagedDTOs = DTOMapper.MapToDTOs(entities);

        ICollection<object> items = string.IsNullOrWhiteSpace(fields)
            ? [.. pagedDTOs.Cast<object>()]
            : [.. QueryFieldService.ShapeCollectionData(pagedDTOs, fields)];

        var paginationResult = new PagedCollectionResult<object>
        {
            Items = items,
            PaginationMetadata = paginationMetadata
        };

        return Result.Success(paginationResult);
    }

    /// <inheritdoc />
    public virtual async Task<Result<IReadOnlyCollection<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        Result validateResult = QueryFieldService.Validate<TEntity>(nameProperty, allowWriteableFields: true);
        if (validateResult.IsFailure)
        {
            var errors = validateResult.Errors
                .Select(e => e with
                {
                    Source = nameof(GetAllForLookupAsync),
                    Target = typeof(TEntity).Name
                })
                .ToList();

            return Result.Failure<IReadOnlyCollection<BaseLookup<TIdentifierType>>>(errors);
        }

        return Result.Success(await Repository.GetAllForLookupAsync(
                nameProperty,
                asTracking: asTracking,
                cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public virtual async Task<Result<TEntity>> GetEntityByIdAsync(
        string idValue,
        string? idField = null,
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        Result validateResult = QueryFieldService.Validate<TEntity>(fields, allowWriteableFields: false);
        if (validateResult.IsFailure)
        {
            var errors = validateResult.Errors
                .Select(e => e with
                {
                    Source = nameof(GetByIdAsync),
                    Target = typeof(TEntity).Name
                })
                .ToList();

            return Result.Failure<TEntity>(errors);
        }

        // Fast path: a plain primary-key lookup skips the dynamic-filter pipeline (see
        // TryGetByIdFastPathAsync). Any other shape falls through to the full pipeline unchanged.
        Result<TEntity>? fastPath = await TryGetByIdFastPathAsync(
            idValue, idField, includeFKs, includeChildren, specification, fields, asTracking, cancellationToken)
            .ConfigureAwait(false);
        if (fastPath is not null)
            return fastPath;

        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            [idField ?? IdField] = ("EQUALS", idValue)
        };

        var (entities, _) = await BuildQueryAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            filters: filters,
            sortColumn: null,
            sortDirection: null,
            fields: fields,
            asTracking: asTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var item = entities.FirstOrDefault();
        if (item is null)
        {
            return Result.Failure<TEntity>(
                Error.NotFound
                    .WithSource(nameof(GetByIdAsync))
                    .WithTarget(typeof(TEntity).Name));
        }

        return Result.Success(item);
    }

    /// <inheritdoc />
    public virtual async Task<Result<object>> GetByIdAsync(
        TIdentifierType id,
        bool includeFKs = false,
        bool includeChildren = false,
        Specification<TEntity, TIdentifierType>? specification = null,
        string? fields = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        var idValue = id.ToString() ?? throw new InvalidOperationException($"The identifier type '{typeof(TIdentifierType).Name}' returned null from ToString().");
        var getResult = await GetEntityByIdAsync(
            idValue: idValue,
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            fields: fields,
            asTracking: asTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (getResult.IsFailure)
            return Result.Failure<object>(getResult.Errors);

        var dto = DTOMapper.MapToDTO(getResult.Value!);

        // Same rule as the list path: only shape when a field subset was requested.
        return string.IsNullOrWhiteSpace(fields)
            ? Result.Success<object>(dto!)
            : Result.Success<object>(QueryFieldService.ShapeData(dto, fields));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
        => Repository.ExistsAsync(where, ignoreQueryFilters, cancellationToken);

    /// <summary>
    /// Assembles the query parameters and delegates execution to the <see cref="IEntityQueryPipeline"/>.
    /// </summary>
    private async Task<(IReadOnlyCollection<TEntity> Items, int TotalCount)> BuildQueryAsync(
        bool includeFKs,
        bool includeChildren,
        Specification<TEntity, TIdentifierType>? specification,
        Dictionary<string, (string Operator, string Value)>? filters,
        string? sortColumn,
        string? sortDirection,
        string? fields,
        int? pageNumber = null,
        int? pageSize = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        var baseQuery = asTracking
            ? Repository.Table
            : Repository.TableNoTracking;

        var navigationMetadata = NavigationMetadataProvider.BuildIncludes<TEntity>(includeFKs, includeChildren);

        var parameters = new EntityQueryParameters<TEntity>
        {
            Criteria = specification?.Criteria,
            Filters = filters,
            SortColumn = sortColumn,
            SortDirection = sortDirection,
            Fields = fields,
            PageNumber = pageNumber,
            PageSize = pageSize,
            IncludeFKs = includeFKs,
            IncludeChildren = includeChildren,
            DTOToEntityPropertyMap = DTOToEntityPropertyMap
        };

        return await QueryPipeline.ExecuteAsync<TEntity, TIdentifierType>(
            baseQuery,
            navigationMetadata,
            parameters,
            NavigationPopulator.PopulateAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private static PaginationMetadata BuildPaginationMetadata(int totalItemCount, int? pageNumber, int? pageSize)
    {
        if (!pageNumber.HasValue || !pageSize.HasValue)
        {
            return new PaginationMetadata
            {
                TotalItemCount = totalItemCount,
                PageSize = totalItemCount,
                CurrentPage = 1
            };
        }

        return new PaginationMetadata
        {
            TotalItemCount = totalItemCount,
            // Report the page size the pipeline actually applied, not the one requested: the
            // pipeline clamps to the framework ceiling, so an over-large request previously
            // advertised a page size the response never contained.
            PageSize = Math.Min(pageSize.Value, Query.EntityQueryPipeline.MaxUnboundedResultLimit),
            CurrentPage = pageNumber.Value
        };
    }
}
