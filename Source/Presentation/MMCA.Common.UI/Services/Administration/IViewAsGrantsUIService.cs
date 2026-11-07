using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The UI's view of the support access log endpoint the framework's
/// <c>ViewAsGrantsAdminControllerBase</c> serves at <c>Admin/ViewAsGrants</c> (ADR-137). Registered by
/// <c>AddSupportAccessLogUI</c>, read by the <c>SupportAccessLog</c> component.
/// </summary>
/// <remarks>
/// Every member answers with a <see cref="Result"/> carrying the API's own errors and their
/// <see cref="ErrorType"/> intact (a host without the grant store answers 404 as a not-found failure),
/// so the component branches on the outcome instead of catching.
/// </remarks>
public interface IViewAsGrantsUIService
{
    /// <summary>Reads one page of view-as grants, from <c>GET Admin/ViewAsGrants</c>.</summary>
    /// <param name="request">The filters and the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or the API's own refusal.</returns>
    Task<Result<PagedCollectionResult<ViewAsGrantSummaryResponse>>> GetPageAsync(
        ViewAsGrantListRequest request,
        CancellationToken cancellationToken = default);
}
