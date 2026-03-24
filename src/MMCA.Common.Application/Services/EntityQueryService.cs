using System.Dynamic;
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

    /// <inheritdoc />
    public virtual async Task<Result<PagedCollectionResult<ExpandoObject>>> GetAllAsync(
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
    public virtual async Task<Result<PagedCollectionResult<ExpandoObject>>> GetAllAsync(
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

            return Result.Failure<PagedCollectionResult<ExpandoObject>>(errors);
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

        // Step 3: Map entities to DTOs and shape output to requested fields
        var paginationMetadata = BuildPaginationMetadata(totalItemCount, pageNumber, pageSize);

        var pagedDTOs = DTOMapper.MapToDTOs(entities);

        var paginationResult = new PagedCollectionResult<ExpandoObject>
        {
            Items = QueryFieldService.ShapeCollectionData(pagedDTOs, fields),
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
    public virtual async Task<Result<ExpandoObject>> GetByIdAsync(
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
            return Result.Failure<ExpandoObject>(getResult.Errors);

        var dto = DTOMapper.MapToDTO(getResult.Value!);

        return Result.Success(QueryFieldService.ShapeData(dto, fields));
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
            PageSize = pageSize.Value,
            CurrentPage = pageNumber.Value
        };
    }
}
