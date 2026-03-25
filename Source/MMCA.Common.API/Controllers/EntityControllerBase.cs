using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.ModelBinders;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Generic read-only controller base providing GET endpoints (all, paged, lookup, by-id) for any entity.
/// Supports field projection via the <c>fields</c> query parameter, server-side filtering, sorting,
/// and pagination with metadata in the <c>X-Pagination</c> response header.
/// </summary>
/// <typeparam name="TEntity">The domain entity type, must inherit from <see cref="AuditableBaseEntity{TIdentifierType}"/>.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned to clients, must implement <see cref="IBaseDTO{TIdentifierType}"/>.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type (e.g., <see langword="int"/>, <see cref="Guid"/>).</typeparam>
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
public abstract class EntityControllerBase<
    TEntity,
    TEntityDTO,
    TIdentifierType>(
    IEntityQueryService<TEntity, TEntityDTO, TIdentifierType> queryService,
    ILogger<EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>> logger)
    : ApiControllerBase, IEntityControllerBase<TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    protected IEntityQueryService<TEntity, TEntityDTO, TIdentifierType> QueryService { get; } = queryService ?? throw new ArgumentNullException(nameof(queryService));

    /// <summary>
    /// Gets the logger instance for derived controllers.
    /// </summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Gets the maximum page size from application settings, falling back to 500.
    /// Resolved per-request from DI to support runtime configuration changes.
    /// </summary>
    protected int MaxPageSize
    {
        get
        {
            var settings = HttpContext.RequestServices.GetService<IApplicationSettings>();
            return settings?.MaxPageSize ?? 500;
        }
    }

    /// <summary>
    /// Gets the entity type name for use in log messages.
    /// </summary>
    protected string EntityName => typeof(TEntity).Name;

    /// <summary>
    /// Returns all entities, optionally with foreign key references, child collections, and field projection.
    /// Capped at <see cref="MaxPageSize"/> results. For larger result sets, use the paged endpoint.
    /// </summary>
    /// <param name="fields">Comma-separated list of DTO property names to include (field projection). Null returns all fields.</param>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collection navigation properties.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of entity DTOs.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<CollectionResult<TEntityDTO>>> GetAllAsync(
        [FromQuery] string? fields = null,
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryService.GetAllAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: null,
            fields: fields,
            pageNumber: 1,
            pageSize: MaxPageSize,
            asTracking: false,
            cancellationToken: cancellationToken);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns a paged, filterable, sortable collection of entities. Pagination metadata is returned
    /// in the <c>X-Pagination</c> response header as JSON. The requested page size is clamped to
    /// <see cref="MaxPageSize"/> to prevent excessive result sets.
    /// </summary>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collection navigation properties.</param>
    /// <param name="sortColumn">DTO property name to sort by.</param>
    /// <param name="sortDirection">Sort direction: "asc" or "desc".</param>
    /// <param name="fields">Comma-separated list of DTO property names for field projection.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Number of items per page (clamped to <see cref="MaxPageSize"/>).</param>
    /// <param name="filters">Query string filters parsed by <see cref="QueryFilterModelBinder"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged collection of entity DTOs with pagination metadata.</returns>
    [HttpGet("paged")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PagedCollectionResult<TEntityDTO>>> GetAllAsync(
        bool includeFKs = false,
        bool includeChildren = false,
        string? sortColumn = null,
        string? sortDirection = null,
        [FromQuery] string? fields = null,
        [Range(1, int.MaxValue)] int pageNumber = 1,
        [Range(1, int.MaxValue)] int pageSize = 10,
        [ModelBinder(typeof(QueryFilterModelBinder))] Dictionary<string, (string Operator, string Value)>? filters = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Min(pageSize, MaxPageSize);

        var result = await QueryService.GetAllAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: null,
            filters: filters,
            sortColumn: sortColumn,
            sortDirection: sortDirection,
            fields: fields,
            pageNumber: pageNumber,
            pageSize: pageSize,
            asTracking: false,
            cancellationToken: cancellationToken);
        if (result.IsFailure)
            return HandleFailure(result.Errors);

        Response.Headers.Append("X-Pagination", JsonSerializer.Serialize(result.Value!.PaginationMetadata, JsonSerializerOptions.Web));
        return Ok(result.Value);
    }

    /// <summary>
    /// Returns a lightweight id/name collection suitable for populating dropdowns and autocomplete controls.
    /// </summary>
    /// <param name="nameProperty">The DTO property name to use as the display label in each lookup entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of <see cref="BaseLookup{TIdentifierType}"/> entries.</returns>
    [HttpGet("lookup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<CollectionResult<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        [Required] string nameProperty,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryService.GetAllForLookupAsync(
            nameProperty,
            asTracking: false,
            cancellationToken: cancellationToken);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(
                new CollectionResult<BaseLookup<TIdentifierType>>
                {
                    Items = [.. result.Value!]
                });
    }

    /// <summary>
    /// Returns a single entity by its identifier.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="includeFKs">When true (default), eagerly loads foreign key navigation properties.</param>
    /// <param name="includeChildren">When true, eagerly loads child collection navigation properties.</param>
    /// <param name="fields">Comma-separated list of DTO property names for field projection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity DTO if found; otherwise a 404 Problem Details response.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TEntityDTO>> GetByIdAsync(
        TIdentifierType id,
        bool includeFKs = true,
        bool includeChildren = false,
        [FromQuery] string? fields = null,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryService.GetByIdAsync(
            id,
            includeFKs,
            includeChildren,
            specification: null,
            fields: fields,
            asTracking: false,
            cancellationToken: cancellationToken);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Extends <see cref="ApiControllerBase.HandleFailure"/> by logging the first error at Warning level
    /// before delegating to the base implementation for Problem Details generation.
    /// </summary>
    /// <param name="errors">The domain errors to convert.</param>
    /// <returns>An <see cref="ObjectResult"/> containing a <see cref="ProblemDetails"/> payload.</returns>
    protected override ObjectResult HandleFailure(IEnumerable<Error> errors)
    {
        var errorList = errors?.ToList() ?? [];
        if (errorList.Count > 0 && Logger.IsEnabled(LogLevel.Warning))
        {
            var firstError = errorList[0];
            Logger.LogWarning(
                "Operation failed for {EntityName}: {ErrorCode} - {ErrorMessage}",
                EntityName,
                firstError.Code,
                firstError.Message);
        }

        return base.HandleFailure(errorList);
    }
}
