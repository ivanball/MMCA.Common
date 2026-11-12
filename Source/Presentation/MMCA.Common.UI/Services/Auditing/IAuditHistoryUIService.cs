using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;

namespace MMCA.Common.UI.Services.Auditing;

/// <summary>
/// Client for an audit-history endpoint (<c>AuditHistoryControllerBase</c>, ADR-075): one page of an
/// entity's recorded changes, newest first. Registered by <c>AddAuditHistoryUI</c> and fed to the
/// <c>AuditHistoryPanel</c>'s <c>FetchPage</c> delegate.
/// </summary>
public interface IAuditHistoryUIService
{
    /// <summary>
    /// Reads one page of the entity's change history from
    /// <c>{routePrefix}/{entityAlias}/{entityKey}?page={page}&amp;pageSize={pageSize}</c>.
    /// </summary>
    /// <param name="routePrefix">The host's audit-history route prefix, for example <c>Admin/Catalog/AuditHistory</c>.</param>
    /// <param name="entityAlias">The audited entity's alias the endpoint knows it by, for example <c>Product</c>.</param>
    /// <param name="entityKey">The entity's key as an invariant string.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page's entries in the order the server sent them, or a failure; never throws for a server answer.</returns>
    Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>> GetPageAsync(
        string routePrefix,
        string entityAlias,
        string entityKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
