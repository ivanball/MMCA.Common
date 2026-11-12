using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the opt-in audit-history endpoint (ADR-075): one page of the recorded change
/// history of one entity, newest change first, read through <see cref="IAuditTrailReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller names the audited entity types it serves and supplies the route and the API
/// version, and nothing else:
/// </para>
/// <code>
/// [Route("Admin/AuditHistory")]
/// [ApiVersion("1.0")]
/// public sealed class AuditHistoryController(IAuditTrailReader reader)
///     : AuditHistoryControllerBase(reader, typeof(Ticket));
/// </code>
/// <para>
/// <b>Route contract.</b> <c>GET {prefix}/{entity}/{key}?page={p}&amp;pageSize={s}</c>, where
/// <c>entity</c> is the entity's simple type name (case-insensitive) and <c>key</c> the invariant string
/// form of its primary key. The 200 body is a bare JSON array of <see cref="AuditHistoryEntryDTO"/>,
/// newest first; the reader has no total, so there is no envelope.
/// </para>
/// <para>
/// <b>Refusals a client has to expect:</b> <see cref="UnknownEntityErrorCode"/> (404) for an entity name
/// the controller does not serve, <see cref="KeyRequiredErrorCode"/> (400) for a blank key, and the
/// automatic 400 of a <c>page</c> or <c>pageSize</c> outside its range. Neither refusal reaches the
/// reader.
/// </para>
/// <para>
/// <c>AddAuditTrail(configuration)</c> registers the reader. The reader reads the host's Default
/// database, so each database-per-service host mounts its own derived controller for the entities it
/// owns. The class-level <see cref="AdministrationPermissions.ViewAuditTrail"/> gate applies to every
/// derived controller, so a forgotten attribute denies rather than exposes.
/// </para>
/// </remarks>
[HasPermission(AdministrationPermissions.ViewAuditTrail)]
public abstract class AuditHistoryControllerBase : ApiControllerBase
{
    /// <summary>Error code of the 404 answered for an entity name the controller does not serve.</summary>
    public const string UnknownEntityErrorCode = "AuditHistory.UnknownEntity";

    /// <summary>Error code of the 400 answered for a blank entity key.</summary>
    public const string KeyRequiredErrorCode = "AuditHistory.KeyRequired";

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditHistoryControllerBase"/> class.
    /// </summary>
    /// <param name="reader">The audit-trail reader.</param>
    /// <param name="auditedEntityTypes">
    /// The entity types this controller serves; each implements <see cref="IAuditedEntity"/>, and no two
    /// share a simple type name (ignoring case), since that name is the route's <c>entity</c> segment.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> or <paramref name="auditedEntityTypes"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="auditedEntityTypes"/> is empty, holds a null entry or a type that is not an
    /// <see cref="IAuditedEntity"/>, or holds two types with the same simple name.
    /// </exception>
    protected AuditHistoryControllerBase(IAuditTrailReader reader, params Type[] auditedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(auditedEntityTypes);

        Reader = reader;
        AuditedEntityTypes = BuildAliasMap(auditedEntityTypes);
    }

    /// <summary>Gets the audit-trail reader (exposed for app-level overrides).</summary>
    protected IAuditTrailReader Reader { get; }

    /// <summary>Gets the served entity types, keyed by simple type name, ignoring case.</summary>
    protected IReadOnlyDictionary<string, Type> AuditedEntityTypes { get; }

    /// <summary>
    /// Returns one page of the recorded change history of one entity, newest change first.
    /// </summary>
    /// <param name="entity">The entity's simple type name, case-insensitive.</param>
    /// <param name="key">The invariant string form of the entity's primary key.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The page size, 1 to 500.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries as a bare array, or a Problem Details refusal.</returns>
    [HttpGet("{entity}/{key}")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditHistoryEntryDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<IReadOnlyList<AuditHistoryEntryDTO>>> GetHistoryAsync(
        string entity,
        string key,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 500)] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (entity is null || !AuditedEntityTypes.TryGetValue(entity, out var entityType))
        {
            return HandleFailure([Error.NotFoundError(
                UnknownEntityErrorCode,
                $"This endpoint serves no audited entity named '{entity}'.",
                nameof(AuditHistoryControllerBase),
                entity)]);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return HandleFailure([Error.Validation(
                KeyRequiredErrorCode,
                "The entity key is required.",
                nameof(AuditHistoryControllerBase),
                nameof(key))]);
        }

        var entries = await Reader.GetForEntityAsync(entityType.FullName!, key, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return Ok(entries);
    }

    /// <summary>Validates the served types and maps each simple name to its type, ignoring case.</summary>
    private static Dictionary<string, Type> BuildAliasMap(Type[] auditedEntityTypes)
    {
        if (auditedEntityTypes.Length == 0)
            throw new ArgumentException("Name at least one audited entity type.", nameof(auditedEntityTypes));

        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in auditedEntityTypes)
        {
            if (type is null)
                throw new ArgumentException("An audited entity type cannot be null.", nameof(auditedEntityTypes));

            if (!typeof(IAuditedEntity).IsAssignableFrom(type))
                throw new ArgumentException($"{type.FullName} does not implement {nameof(IAuditedEntity)}, so it has no recorded history.", nameof(auditedEntityTypes));

            if (!map.TryAdd(type.Name, type))
                throw new ArgumentException($"Two audited entity types share the name '{type.Name}', which would make the route ambiguous.", nameof(auditedEntityTypes));
        }

        return map;
    }
}
