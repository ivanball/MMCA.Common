using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the support access log (ADR-137): every view-as grant in one place, who viewed
/// whom, why, when and how it ended, newest first and paged. View only.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the API version, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/ViewAsGrants")]
/// [ApiVersion("1.0")]
/// public sealed class AdminViewAsGrantsController : ViewAsGrantsAdminControllerBase;
/// </code>
/// <para>
/// The route lives on the subclass (the <see cref="UsersAdminControllerBase{TUserDto}"/> precedent)
/// because the app owns its URL space. Mount it on the service that holds the grant store: on a host
/// with <c>ViewAs:Enabled</c> false, or on a service of a multi-service host that does not hold the
/// store (<c>ViewAs:HostsGrantStore</c> false), it answers 404, as the grant and end actions do.
/// </para>
/// <para>
/// <b>Gated on <see cref="AdministrationPermissions.ManageUsers"/></b>, the permission the user
/// administration endpoints and the per-account sign-in history already require, so whoever can open
/// an account's history can open the log of every account's.
/// </para>
/// </remarks>
[HasPermission(AdministrationPermissions.ManageUsers)]
public abstract class ViewAsGrantsAdminControllerBase : ApiControllerBase
{
    /// <summary>
    /// Returns one page of view-as grants, newest first, narrowed by the request's filters (staff and
    /// viewed email text, a granted-at window, a derived status). The page size is clamped to
    /// <see cref="ViewAsGrantListRequest.MaxPageSize"/>.
    /// </summary>
    /// <param name="request">The filters and the page, from the query string.</param>
    /// <param name="settings">The view-as settings, resolved per action.</param>
    /// <param name="grants">The grant store, resolved per action.</param>
    /// <param name="timeProvider">The clock the status is derived at, resolved per action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or 404 on a host without the grant store.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedCollectionResult<ViewAsGrantSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PagedCollectionResult<ViewAsGrantSummaryResponse>>> ListAsync(
        [FromQuery] ViewAsGrantListRequest request,
        [FromServices] IOptions<ViewAsSettings> settings,
        [FromServices] IViewAsGrantStore grants,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!settings.Value.Enabled || !settings.Value.HostsGrantStore)
        {
            return NotFound();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (items, total) = await grants.ListAsync(request, now, cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<ViewAsGrantSummaryResponse> rows =
        [
            .. items.Select(g => new ViewAsGrantSummaryResponse(
                g.Id,
                g.StaffUserId,
                g.StaffDisplay,
                g.TargetUserId,
                g.TargetDisplay,
                g.Reason,
                g.GrantedAt,
                g.ExpiresAt,
                g.EndedAt,
                g.StatusAt(now))),
        ];

        // The metadata reports the page actually served: the store clamps the same way.
        var metadata = new PaginationMetadata(
            total,
            Math.Clamp(request.PageSize, 1, ViewAsGrantListRequest.MaxPageSize),
            Math.Max(request.PageNumber, 1));

        return Ok(new PagedCollectionResult<ViewAsGrantSummaryResponse>(rows, metadata));
    }
}
