using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Export;
using MMCA.Common.API.ModelBinders;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;

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
            var settings = HttpContext.RequestServices.GetService<IOptions<ApplicationSettings>>()?.Value;
            return settings?.MaxPageSize ?? 500;
        }
    }

    /// <summary>
    /// Gets the maximum number of rows <see cref="ExportAsync"/> streams before it stops and appends
    /// its truncation marker, from application settings, falling back to
    /// <see cref="DefaultMaxExportRows"/>. Resolved per-request from DI exactly like
    /// <see cref="MaxPageSize"/>.
    /// </summary>
    /// <remarks>
    /// A configured value of zero or less is treated as "not configured" and falls back to the
    /// default: a cap of zero would silently serve every caller a header-only file, which is a far
    /// worse failure than ignoring a nonsensical setting.
    /// </remarks>
    protected int MaxExportRows
    {
        get
        {
            var settings = HttpContext.RequestServices.GetService<IOptions<ApplicationSettings>>()?.Value;
            var configured = settings?.MaxExportRows ?? DefaultMaxExportRows;
            return configured > 0 ? configured : DefaultMaxExportRows;
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
    /// <remarks>
    /// The rows are scoped by <see cref="GetReadSpecificationAsync"/>, which returns
    /// <see langword="null"/> by default and so queries unscoped exactly as this endpoint always has.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<CollectionResult<TEntityDTO>>> GetAllAsync(
        [FromQuery] string? fields = null,
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default)
    {
        var specification = await GetReadSpecificationAsync(cancellationToken).ConfigureAwait(false);

        var result = await QueryService.GetAllAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            fields: fields,
            pageNumber: 1,
            pageSize: MaxPageSize,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns a paged, filterable, sortable collection of entities. Pagination metadata is returned
    /// in the <c>X-Pagination</c> response header as JSON. The requested page size is clamped to
    /// <see cref="MaxPageSize"/> to prevent excessive result sets.
    /// </summary>
    /// <remarks>
    /// The rows are scoped by <see cref="GetReadSpecificationAsync"/>. The specification is ANDed
    /// with the caller's <paramref name="filters"/> by the query service rather than replacing them,
    /// so a caller cannot widen the scope by filtering: a filter naming rows the specification
    /// excludes yields an empty page instead of leaking them.
    /// </remarks>
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

        var specification = await GetReadSpecificationAsync(cancellationToken).ConfigureAwait(false);

        var result = await QueryService.GetAllAsync(
            includeFKs: includeFKs,
            includeChildren: includeChildren,
            specification: specification,
            filters: filters,
            sortColumn: sortColumn,
            sortDirection: sortDirection,
            fields: fields,
            pageNumber: pageNumber,
            pageSize: pageSize,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return HandleFailure(result.Errors);

        Response.Headers.Append("X-Pagination", JsonSerializer.Serialize(result.Value!.PaginationMetadata, JsonSerializerOptions.Web));
        return Ok(result.Value);
    }

    /// <summary>
    /// Streams the same collection the paged endpoint serves as an RFC 4180 CSV file download,
    /// page-looping the query service server-side so a client gets the whole filtered set in one
    /// request instead of walking pages itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a distinct route rather than content negotiation.</b> Serving CSV from
    /// <c>GET /{controller}/paged</c> on an <c>Accept: text/csv</c> header would be wrong twice over
    /// in this framework: the public output-cache policy varies by query string but NOT by
    /// <c>Accept</c>, so a cached JSON body could be replayed to a CSV request; and <c>AddAPI</c> sets
    /// <c>ReturnHttpNotAcceptable = false</c>, so a negotiation miss falls back to JSON silently
    /// instead of returning 406. A separate path has neither failure mode.
    /// </para>
    /// <para>
    /// <b>Authorization.</b> This action carries no attributes of its own: it inherits whatever the
    /// concrete controller declares, so an <c>[Authorize]</c> or <c>[FeatureGate]</c> on the derived
    /// class governs the export exactly as it governs the paged read. A controller that exposes
    /// <c>paged</c> anonymously exposes <c>export</c> anonymously; that symmetry is deliberate, since
    /// the two return the same rows.
    /// </para>
    /// <para>
    /// <b>Row ceiling and the truncation signal.</b> Every response carries
    /// <c>X-Export-Row-Limit</c> up front, and a truncated export ends with a final
    /// <c>{"# export truncated at N rows"}</c> line. The ceiling cannot be reported as a
    /// response header, because it is only known after the last page is read and headers are frozen
    /// the moment the first body byte is flushed. Buffering the whole file to learn the answer first
    /// would defeat the streaming this endpoint exists for, so the marker rides in the body where it
    /// is still writable.
    /// </para>
    /// <para>
    /// <b>Child collections.</b> Unlike the paged endpoint this takes no <c>includeChildren</c>: a
    /// child collection has no faithful representation in a flat CSV cell. For the same reason the
    /// column set drops any DTO property that cannot render a faithful scalar cell (binary tokens
    /// such as <c>rowVersion</c>, and every collection-typed property), so those columns are absent
    /// rather than filled with a type name. A <c>fields=</c> request that names one of them is a
    /// validation failure, not a silent omission.
    /// </para>
    /// <para>
    /// <b>Row scoping.</b> The rows queried here are whatever
    /// <see cref="GetReadSpecificationAsync"/> allows, which is the same hook the list endpoints
    /// read, so an export can no longer drift wider than the list it mirrors. The default returns
    /// <see cref="GetExportSpecification"/>, itself null by default, so an export is unscoped unless
    /// the concrete controller overrides one of the two.
    /// </para>
    /// </remarks>
    /// <param name="includeFKs">When true, eagerly loads foreign key navigation properties.</param>
    /// <param name="sortColumn">DTO property name to sort by.</param>
    /// <param name="sortDirection">Sort direction: "asc" or "desc".</param>
    /// <param name="fields">Comma-separated list of DTO property names for field projection. The CSV
    /// columns are the camelCase JSON names, so the same <c>fields=</c> request produces the same
    /// column set here and on the JSON endpoints.</param>
    /// <param name="filters">Query string filters parsed by <see cref="QueryFilterModelBinder"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An empty result: the CSV has already been written to the response body. A failure on
    /// the FIRST page (before any byte is written) returns Problem Details instead.</returns>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> ExportAsync(
        bool includeFKs = false,
        string? sortColumn = null,
        string? sortDirection = null,
        [FromQuery] string? fields = null,
        [ModelBinder(typeof(QueryFilterModelBinder))] Dictionary<string, (string Operator, string Value)>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var unexportableFieldErrors = EntityCsvExporter<TEntityDTO>.ValidateFields(fields);
        if (unexportableFieldErrors is not null)
            return HandleFailure(unexportableFieldErrors);

        // Resolved once, so every page of the loop is filtered by the same instance.
        var specification = await GetReadSpecificationAsync(cancellationToken).ConfigureAwait(false);

        var maxExportRows = MaxExportRows;
        var pageSize = Math.Max(1, MaxPageSize);

        var written = await EntityCsvExporter<TEntityDTO>.WriteAsync(
            Response.Body,
            (pageNumber, token) => QueryService.GetAllAsync(
                includeFKs: includeFKs,
                includeChildren: false,
                specification: specification,
                filters: filters,
                sortColumn: sortColumn,
                sortDirection: sortDirection,
                fields: fields,
                pageNumber: pageNumber,
                pageSize: pageSize,
                asTracking: false,
                cancellationToken: token),
            pageSize,
            maxExportRows,
            fields,
            beginResponse: () => BeginExportResponse(maxExportRows),
            onFailureAfterStart: LogExportPageFailure,
            cancellationToken).ConfigureAwait(false);

        // A failure here is the FIRST page's: nothing has been written, so Problem Details still works.
        return written.IsFailure ? HandleFailure(written.Errors) : new EmptyResult();
    }

    /// <summary>
    /// Returns a lightweight id/name collection suitable for populating dropdowns and autocomplete controls.
    /// </summary>
    /// <param name="nameProperty">The DTO property name to use as the display label in each lookup entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of <see cref="BaseLookup{TIdentifierType}"/> entries.</returns>
    /// <remarks>
    /// The lookup contract takes a predicate rather than a specification, so the scope from
    /// <see cref="GetReadSpecificationAsync"/> travels as its <c>Criteria</c> expression. A null
    /// specification passes a null predicate, which is exactly the unscoped query this endpoint has
    /// always issued. A dropdown that lists what the list endpoint hides would be an existence
    /// oracle of its own, which is why this action reads the same hook as the rest.
    /// </remarks>
    [HttpGet("lookup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<CollectionResult<BaseLookup<TIdentifierType>>>> GetAllForLookupAsync(
        [Required] string nameProperty,
        CancellationToken cancellationToken = default)
    {
        var specification = await GetReadSpecificationAsync(cancellationToken).ConfigureAwait(false);

        var result = await QueryService.GetAllForLookupAsync(
            nameProperty,
            where: specification?.Criteria,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// The read is scoped by <see cref="GetReadSpecificationAsync"/>, and a row the specification
    /// excludes is a <b>404</b>, not a 403: the specification narrows the query, so the row is
    /// simply not there to find. Answering "forbidden" would confirm that the id exists, turning a
    /// scoped read into an existence oracle a caller could walk.
    /// </remarks>
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
        var specification = await GetReadSpecificationAsync(cancellationToken).ConfigureAwait(false);

        var result = await QueryService.GetByIdAsync(
            id,
            includeFKs,
            includeChildren,
            specification: specification,
            fields: fields,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
            return HandleFailure(result.Errors);

        SetConcurrencyETag(result.Value);
        return Ok(result.Value);
    }

    /// <summary>
    /// The DTO's <c>RowVersion</c> property when it carries one, resolved once per closed controller
    /// type because a DTO's shape cannot change at runtime. Null for a DTO with no concurrency token,
    /// which makes <see cref="SetConcurrencyETag"/> a no-op with no per-request reflection at all.
    /// </summary>
    private static readonly PropertyInfo? RowVersionProperty = typeof(TEntityDTO)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(p => p.CanRead
            && p.PropertyType == typeof(byte[])
            && string.Equals(p.Name, "RowVersion", StringComparison.Ordinal));

    /// <summary>
    /// Emits the read's concurrency token as a weak <c>ETag</c>, so a client can hand it straight back
    /// as an <c>If-Match</c> precondition on the next write (see
    /// <see cref="Concurrency.SupportsIfMatchAttribute"/>), which is the only route a token travels.
    /// </summary>
    /// <param name="dto">The DTO just served, or null: a typed DTO on an ordinary read, or a shaped
    /// dictionary keyed by JSON names when the caller asked for a field projection.</param>
    /// <remarks>
    /// <para>
    /// A DTO without a <c>RowVersion</c> gets no header: absent is the correct answer for a resource
    /// that has no version to condition on, and a fabricated tag would invite preconditions the write
    /// side cannot honour.
    /// </para>
    /// <para>
    /// Protected rather than private so a derived controller that serves a row from somewhere other
    /// than <see cref="GetByIdAsync"/> (a custom read action, a projection built by hand) can emit
    /// the same header instead of re-implementing the reflection and the base64 format, which is
    /// where the two drift apart and an <c>If-Match</c> precondition quietly stops working.
    /// </para>
    /// </remarks>
    protected void SetConcurrencyETag(object? dto)
    {
        if (RowVersionProperty is null || dto is null)
            return;

        if (ReadRowVersion(dto) is not { Length: > 0 } rowVersion)
            return;

        Response.Headers[ConcurrencyETag.ETagHeaderName] = ConcurrencyETag.Format(rowVersion);
    }

    /// <summary>
    /// Reads the concurrency token off a served row, which is a typed DTO on an ordinary read and a
    /// shaped dictionary keyed by JSON names when the caller asked for a field projection.
    /// </summary>
    /// <param name="dto">The row just served.</param>
    /// <returns>The token, or null when this row carries none.</returns>
    private static byte[]? ReadRowVersion(object dto)
    {
        if (RowVersionProperty!.DeclaringType!.IsInstanceOfType(dto))
            return RowVersionProperty.GetValue(dto) as byte[];

        return dto is IDictionary<string, object?> shaped
            && (shaped.TryGetValue("rowVersion", out var value) || shaped.TryGetValue("RowVersion", out value))
            ? value as byte[]
            : null;
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

    /// <summary>
    /// Row ceiling used by <see cref="ExportAsync"/> when no host setting supplies one. Matches
    /// <see cref="ApplicationSettings.MaxExportRows"/>'s own default so an unconfigured host and a
    /// default-configured host behave identically.
    /// </summary>
    protected const int DefaultMaxExportRows = 100_000;

    /// <summary>The media type <see cref="ExportAsync"/> serves.</summary>
    public const string CsvContentType = "text/csv; charset=utf-8";

    /// <summary>
    /// Response header naming the row ceiling in force for this export. Always sent, so a client can
    /// tell "exactly at the limit" from "coincidentally that many rows" without parsing the body.
    /// </summary>
    public const string ExportRowLimitHeaderName = "X-Export-Row-Limit";

    /// <summary>
    /// Gets the file name stem for an export download: the routed controller name (so
    /// <c>ProductsController</c> downloads <c>Products-...csv</c>), falling back to the entity type
    /// name in the degenerate case of a controller class named nothing else.
    /// </summary>
    /// <remarks>
    /// Derived from the concrete type name rather than read out of <c>RouteData</c>, because that is
    /// precisely what the <c>[controller]</c> route token resolves to, and it stays correct when the
    /// action is invoked outside a routed request.
    /// </remarks>
    protected virtual string ExportFileNamePrefix
    {
        get
        {
            const string suffix = "Controller";

            var typeName = GetType().Name;
            var stem = typeName.EndsWith(suffix, StringComparison.Ordinal)
                ? typeName[..^suffix.Length]
                : typeName;

            return string.IsNullOrWhiteSpace(stem) ? EntityName : stem;
        }
    }

    /// <summary>
    /// Gets the specification every read action applies: both <see cref="GetAllAsync(string, bool, bool, CancellationToken)"/>
    /// overloads, <see cref="GetAllForLookupAsync"/>, <see cref="GetByIdAsync"/> and
    /// <see cref="ExportAsync"/>. Returns <see cref="GetExportSpecification"/> by default, itself
    /// <see langword="null"/>, so a controller that overrides neither queries unscoped exactly as
    /// these endpoints always have.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The specification the read is filtered by, or <see langword="null"/> for an unscoped read.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why asynchronous.</b> Row scoping is rarely a pure function of the current principal: it
    /// is resolved through a query handler, a claim lookup that hits a store, a tenancy read. A
    /// synchronous hook forced every such controller to override all five actions by hand purely to
    /// get an <see langword="await"/> in before the query, which is exactly the duplication (and the
    /// export-drifts-from-the-list hazard) this hook removes. An override that has nothing to await
    /// returns <c>ValueTask.FromResult(...)</c> and allocates nothing.
    /// </para>
    /// <para>
    /// <b>How it composes.</b> The specification never replaces the caller's own filtering: the
    /// query service ANDs it with the <c>filters</c> dictionary and the <c>fields</c> projection, so
    /// a caller can only narrow what the specification already allows. The lookup endpoint has no
    /// specification parameter, so it receives the same scope as the specification's <c>Criteria</c>
    /// predicate.
    /// </para>
    /// <para>
    /// <b>What a rejected row looks like.</b> A scoped <see cref="GetByIdAsync"/> that filters the
    /// row out returns <b>404</b>, not 403: the row is absent from the query, and a "forbidden"
    /// answer would confirm the id exists.
    /// </para>
    /// <para>
    /// Called once per request, before the first query, so one instance filters every page of an
    /// export loop. Return a specification that is safe to reuse across those queries.
    /// </para>
    /// </remarks>
    protected virtual ValueTask<Specification<TEntity, TIdentifierType>?> GetReadSpecificationAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetExportSpecification());

    /// <summary>
    /// Gets the specification <see cref="ExportAsync"/> applies to every page it streams. Returns
    /// <see langword="null"/> by default, which queries unscoped exactly as the endpoint always has.
    /// </summary>
    /// <returns>The specification every export page is filtered by, or <see langword="null"/> for an unscoped export.</returns>
    /// <remarks>
    /// <para>
    /// The synchronous half of <see cref="GetReadSpecificationAsync"/>, which returns this by
    /// default. A controller that can build its scope without awaiting anything overrides this one
    /// and gets all five read actions scoped; a controller that needs an <see langword="await"/> overrides
    /// <see cref="GetReadSpecificationAsync"/> instead, and then this hook is no longer consulted
    /// (the override replaces the default that called it).
    /// </para>
    /// <para>
    /// A controller whose list endpoints row-scope reads (an ownership specification, a tenancy
    /// predicate, anything that decides which rows this caller may see) MUST override one of the two,
    /// so <c>/export</c> shows exactly what the list shows. Leaving both at the default on such a
    /// controller hands every caller the whole table in one request.
    /// </para>
    /// <para>
    /// A controller that overrides this can then relax any privileged-role gate it put on the export
    /// as an interim mitigation: with the specification in force it is the query, not the role, that
    /// keeps one caller out of another caller's rows, and an owner can export their own data again.
    /// </para>
    /// </remarks>
    protected virtual Specification<TEntity, TIdentifierType>? GetExportSpecification() => null;

    /// <summary>
    /// Sets the response status headers for an export, before the first body byte goes out.
    /// </summary>
    /// <param name="maxExportRows">The row ceiling in force, advertised as <see cref="ExportRowLimitHeaderName"/>.</param>
    private void BeginExportResponse(int maxExportRows)
    {
        var timeProvider = HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

        Response.ContentType = CsvContentType;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{BuildExportFileName(timeProvider.GetUtcNow())}\"";
        Response.Headers.Append(ExportRowLimitHeaderName, maxExportRows.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds the download file name: <c>{controller}-{yyyyMMddTHHmmssZ}.csv</c>. The timestamp is UTC
    /// in a basic-format ISO 8601 shape (no separators, so it is legal in a file name on every OS) and
    /// invariant, so exports sort chronologically by name in any locale.
    /// </summary>
    /// <param name="timestamp">When the export was produced.</param>
    /// <returns>The file name offered in the Content-Disposition header.</returns>
    protected virtual string BuildExportFileName(DateTimeOffset timestamp) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ExportFileNamePrefix}-{timestamp.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.csv");

    /// <summary>
    /// Logs a query failure that arrived after the export body had already started, where
    /// <see cref="HandleFailure"/> is no longer an option.
    /// </summary>
    /// <param name="errors">The errors reported by the query service.</param>
    /// <param name="rowsWritten">How many data rows had been written.</param>
    private void LogExportPageFailure(IReadOnlyList<Error> errors, int rowsWritten)
    {
        if (!Logger.IsEnabled(LogLevel.Warning))
            return;

        var firstError = errors.Count > 0 ? errors[0] : null;
        Logger.LogWarning(
            "Export of {EntityName} stopped after {RowCount} rows: {ErrorCode} - {ErrorMessage}",
            EntityName,
            rowsWritten,
            firstError?.Code,
            firstError?.Message);
    }
}
