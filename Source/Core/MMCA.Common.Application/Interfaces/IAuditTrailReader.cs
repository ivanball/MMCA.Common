using MMCA.Common.Application.Auditing;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Reads the recorded change history of one entity. Registered by <c>AddAuditTrail</c>; a host that
/// never opted in has no implementation to resolve.
/// </summary>
/// <remarks>
/// <para>
/// The framework ships the read, not the exposure: there is no shipped endpoint or page in v1,
/// because who may see an entity's history is an application decision (an admin screen, a support
/// tool, a data-subject request) rather than a framework one. Consumers wrap this in whatever query
/// and authorization their domain calls for.
/// </para>
/// <para>
/// Rows come back newest first, so the first page is the most recent activity.
/// </para>
/// </remarks>
public interface IAuditTrailReader
{
    /// <summary>
    /// Returns one page of the change history recorded for a single entity, newest change first.
    /// </summary>
    /// <param name="entityType">
    /// The full CLR type name of the entity, as recorded on the trail row (for example
    /// <c>typeof(Order).FullName</c>).
    /// </param>
    /// <param name="entityKey">
    /// The invariant string form of the entity's primary key; the parts of a composite key joined
    /// with <c>|</c> in the model's key order.
    /// </param>
    /// <param name="page">The 1-based page number; values below 1 are treated as 1.</param>
    /// <param name="pageSize">The page size; values below 1 are treated as 1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded changes for that entity, or an empty list when it has no history.</returns>
    Task<IReadOnlyList<AuditTrailEntryDTO>> GetForEntityAsync(
        string entityType,
        string entityKey,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}
