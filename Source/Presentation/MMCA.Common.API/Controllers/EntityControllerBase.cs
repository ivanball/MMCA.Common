using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Export;
using MMCA.Common.API.ModelBinders;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
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
            var settings = HttpContext.RequestServices.GetService<IApplicationSettings>();
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
    /// <see cref="GetExportSpecification"/> allows. The default returns null, so an export is
    /// unscoped unless the concrete controller says otherwise; a controller whose list endpoints
    /// row-scope reads must override that hook.
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
        var unexportableFieldErrors = ValidateExportFields(fields);
        if (unexportableFieldErrors is not null)
            return HandleFailure(unexportableFieldErrors);

        // Resolved once, so every page of the loop is filtered by the same instance.
        var specification = GetExportSpecification();

        var maxExportRows = MaxExportRows;
        var pageSize = Math.Max(1, MaxPageSize);
        var pageNumber = 1;
        var rowsWritten = 0;
        var truncated = false;
        var started = false;
        IReadOnlyList<string> columns = [];

        // Constructing the writer sends nothing: the response stays uncommitted until the first
        // flush below, which is what keeps the "failure on page one still returns Problem Details"
        // path honest.
        var writer = new StreamWriter(Response.Body, CsvWriter.Utf8NoPreamble, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            while (true)
            {
                var result = await QueryService.GetAllAsync(
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
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    if (!started)
                        return HandleFailure(result.Errors);

                    // The status line left the building with the header row. A trailing marker is the
                    // only signal still available, so the file says it is short rather than looking
                    // like a complete export of fewer rows.
                    LogExportPageFailure(result.Errors, rowsWritten);
                    CsvWriter.WriteRow([IncompleteMarker(rowsWritten)], writer);
                    break;
                }

                var page = result.Value!;
                var items = page.Items;

                if (!started)
                {
                    columns = ResolveExportColumns(items, fields);
                    BeginExportResponse(maxExportRows);
                    CsvWriter.WriteByteOrderMark(writer);
                    CsvWriter.WriteHeader(columns, writer);
                    started = true;
                }

                var pageRows = WriteExportPage(items, columns, fields, maxExportRows - rowsWritten, writer);
                rowsWritten += pageRows;

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                // Stopped mid-page: rows were definitely left behind.
                if (pageRows < items.Count)
                {
                    truncated = true;
                    break;
                }

                // A short page is the last page, cap or no cap.
                if (items.Count < pageSize)
                    break;

                // The cap landed exactly on a page boundary: only the total says whether anything
                // remains, and asking for one more page just to find out would be a wasted query.
                if (rowsWritten >= maxExportRows)
                {
                    truncated = page.PaginationMetadata.TotalItemCount > rowsWritten;
                    break;
                }

                pageNumber++;
            }

            if (truncated)
            {
                CsvWriter.WriteRow([TruncationMarker(rowsWritten)], writer);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
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
            cancellationToken: cancellationToken).ConfigureAwait(false);

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
    /// Gets the specification <see cref="ExportAsync"/> applies to every page it streams. Returns
    /// <see langword="null"/> by default, which queries unscoped exactly as the endpoint always has.
    /// </summary>
    /// <returns>The specification every export page is filtered by, or <see langword="null"/> for an unscoped export.</returns>
    /// <remarks>
    /// <para>
    /// A controller whose list endpoints row-scope reads (an ownership specification, a tenancy
    /// predicate, anything that decides which rows this caller may see) MUST override this with the
    /// same specification, so <c>/export</c> shows exactly what the list shows. Leaving it at the
    /// default on such a controller hands every caller the whole table in one request.
    /// </para>
    /// <para>
    /// A controller that overrides this can then relax any privileged-role gate it put on the export
    /// as an interim mitigation: with the specification in force it is the query, not the role, that
    /// keeps one caller out of another caller's rows, and an owner can export their own data again.
    /// </para>
    /// <para>
    /// Called once per request, before the first page, so the same instance filters every page of
    /// the loop. Return a specification that is safe to reuse across those queries.
    /// </para>
    /// </remarks>
    protected virtual Specification<TEntity, TIdentifierType>? GetExportSpecification() => null;

    /// <summary>
    /// The DTO property names an export omits, because their values cannot render a faithful scalar
    /// CSV cell: binary concurrency tokens (<see langword="byte"/>[], <see cref="ReadOnlyMemory{T}"/>
    /// of bytes) and every collection-typed property except <see langword="string"/>. Computed once
    /// per closed controller type, since a DTO's shape cannot change at runtime.
    /// </summary>
    /// <remarks>
    /// Value objects and other class-typed properties are deliberately NOT dropped: a record or
    /// value object has a meaningful invariant <c>ToString</c>, which is exactly the cell a reader
    /// expects. Only the two categories above render a type name instead of a value.
    /// </remarks>
    private static readonly string[] UnexportablePropertyNames =
    [
        .. typeof(TEntityDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && !IsExportableType(p.PropertyType))
            .Select(p => p.Name)
    ];

    /// <summary>
    /// <see cref="UnexportablePropertyNames"/> as the camelCase column names a shaped row carries,
    /// for filtering a resolved column list.
    /// </summary>
    private static readonly HashSet<string> UnexportableColumns =
        [.. UnexportablePropertyNames.Select(JsonNamingPolicy.CamelCase.ConvertName)];

    /// <summary>
    /// <see cref="UnexportablePropertyNames"/> as a case-insensitive set, for matching the names a
    /// caller spells in <c>fields=</c>.
    /// </summary>
    private static readonly HashSet<string> UnexportableFields =
        new(UnexportablePropertyNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a DTO property type can render a faithful scalar CSV cell. See
    /// <see cref="UnexportablePropertyNames"/> for what the two exclusions buy.
    /// </summary>
    /// <param name="type">The declared property type.</param>
    /// <returns><see langword="true"/> when the property earns a column.</returns>
    private static bool IsExportableType(Type type) =>
        type != typeof(byte[])
        && type != typeof(ReadOnlyMemory<byte>)
        && (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type));

    /// <summary>Whether a resolved column survives the exclusions in <see cref="UnexportableColumns"/>.</summary>
    /// <param name="column">The camelCase column name.</param>
    /// <returns><see langword="true"/> when the column is written.</returns>
    private static bool IsExportableColumn(string column) => !UnexportableColumns.Contains(column);

    /// <summary>
    /// Rejects a <c>fields=</c> request that names a property the export cannot render. Answering
    /// such a request with the column quietly missing would hide the contract from the caller, so it
    /// fails the same way an unknown field does on the JSON endpoints: an
    /// <c>Error.InvalidEntityField</c> validation failure, before a single byte of body is written.
    /// </summary>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The validation errors, or <see langword="null"/> when the request is clean.</returns>
    private static List<Error>? ValidateExportFields(string? fields)
    {
        if (UnexportableFields.Count == 0)
            return null;

        List<Error> errors =
        [
            .. ParseExportFields(fields)
                .Where(UnexportableFields.Contains)
                .Select(field => Error.InvalidEntityField with
                {
                    Message = $"Field '{field}' on type '{typeof(TEntityDTO).Name}' cannot be exported to CSV: binary and collection properties have no faithful CSV representation.",
                    Target = typeof(TEntityDTO).Name
                })
        ];

        return errors.Count > 0 ? errors : null;
    }

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
    /// Writes one page of results as CSV records, stopping early once <paramref name="remainingRows"/>
    /// have been written (the row ceiling landing inside this page).
    /// </summary>
    /// <param name="items">The page of rows.</param>
    /// <param name="columns">The column names, in output order.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <param name="remainingRows">How many more rows the export may still write.</param>
    /// <param name="writer">The destination writer.</param>
    /// <returns>The number of rows written, which is short of the page when the ceiling was reached.</returns>
    private static int WriteExportPage(
        ICollection<object> items,
        IReadOnlyList<string> columns,
        string? fields,
        int remainingRows,
        TextWriter writer)
    {
        var written = 0;

        foreach (var item in items)
        {
            if (written >= remainingRows)
                break;

            CsvWriter.WriteRow(BuildExportCells(ShapeExportRow(item, fields), columns), writer);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Resolves the CSV column names for an export from the first page of results, falling back to the
    /// DTO's own shape when that page is empty (an export of nothing still owes the caller a header
    /// row naming the columns).
    /// </summary>
    /// <remarks>
    /// Both paths drop the columns named in <see cref="UnexportableColumns"/>: the shaped-row path
    /// filters the row's own keys, the empty-page path filters the DTO's properties by type. A
    /// dropped property yields no column at all rather than an empty one, so a reader never sees a
    /// header it cannot trust.
    /// </remarks>
    /// <param name="items">The first page of rows, typed DTOs or already-shaped dynamic objects.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The column names, in output order, as camelCase JSON names.</returns>
    private static IReadOnlyList<string> ResolveExportColumns(ICollection<object> items, string? fields)
    {
        var first = items.FirstOrDefault();
        if (first is not null)
            return [.. ShapeExportRow(first, fields).Keys.Where(IsExportableColumn)];

        var requested = ParseExportFields(fields);

        // Property declaration order, filtered by the requested fields: exactly the order
        // QueryFieldService.ShapeCollectionData would have produced had there been a row to shape.
        return
        [
            .. typeof(TEntityDTO)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                    && IsExportableType(p.PropertyType)
                    && (requested.Count == 0 || requested.Contains(p.Name)))
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
        ];
    }

    /// <summary>
    /// Normalizes one result row to a camelCase-keyed dictionary. The query service already returns
    /// shaped dynamic objects when a field subset was requested, and typed DTOs otherwise, so this
    /// shapes only the second case and reuses <see cref="QueryFieldService"/>'s compiled accessors to
    /// do it. Either way the keys are the JSON property names.
    /// </summary>
    /// <param name="item">The row to normalize.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The row as a name-to-value dictionary.</returns>
    private static IDictionary<string, object?> ShapeExportRow(object item, string? fields) =>
        item as IDictionary<string, object?>
        ?? QueryFieldService.ShapeData((TEntityDTO)item, fields);

    /// <summary>
    /// Projects a shaped row onto the resolved column order, substituting null for a column the row
    /// does not carry.
    /// </summary>
    /// <param name="row">The shaped row.</param>
    /// <param name="columns">The column names, in output order.</param>
    /// <returns>The cell values for one CSV record.</returns>
    private static object?[] BuildExportCells(IDictionary<string, object?> row, IReadOnlyList<string> columns)
    {
        var cells = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            cells[i] = row.TryGetValue(columns[i], out var value) ? value : null;
        }

        return cells;
    }

    /// <summary>Splits a comma-separated <c>fields=</c> value into a case-insensitive set.</summary>
    /// <param name="fields">The requested field projection, or null.</param>
    /// <returns>The requested field names; empty when none were requested.</returns>
    private static HashSet<string> ParseExportFields(string? fields) =>
        fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    /// <summary>The final line appended to an export stopped by the row ceiling.</summary>
    /// <param name="rowsWritten">How many data rows were written.</param>
    /// <returns>The marker text, written as a single-cell record.</returns>
    private static string TruncationMarker(int rowsWritten) =>
        string.Create(CultureInfo.InvariantCulture, $"# export truncated at {rowsWritten} rows");

    /// <summary>The final line appended to an export abandoned by a query failure mid-stream.</summary>
    /// <param name="rowsWritten">How many data rows were written before the failure.</param>
    /// <returns>The marker text, written as a single-cell record.</returns>
    private static string IncompleteMarker(int rowsWritten) =>
        string.Create(CultureInfo.InvariantCulture, $"# export incomplete after {rowsWritten} rows");

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
