using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// The support access log's filters as one translatable predicate chain (ADR-137), shared by every
/// <see cref="IViewAsGrantStore"/> so the EF store and the in-memory one in <c>MMCA.Common.Testing</c>
/// cannot disagree on what a filter means. Ordering and paging stay with the store.
/// </summary>
public static class ViewAsGrantLogQuery
{
    /// <summary>
    /// Narrows <paramref name="grants"/> by every filter <paramref name="request"/> sets, combined with
    /// AND: the staff and viewed email text (a contains match against the display snapshots, the
    /// needle folded by <see cref="ViewAsGrant.NormalizeDisplay"/> to the form they are stored in), the
    /// granted-at window (<see cref="ViewAsGrantListRequest.From"/> inclusive,
    /// <see cref="ViewAsGrantListRequest.To"/> exclusive), and the status derived at
    /// <paramref name="now"/> exactly as <see cref="ViewAsGrant.StatusAt"/> derives it.
    /// </summary>
    /// <param name="grants">The grants to filter (an EF set or an in-memory sequence).</param>
    /// <param name="request">The filters.</param>
    /// <param name="now">The UTC instant the status is derived at.</param>
    /// <returns>The filtered query, unordered.</returns>
    public static IQueryable<ViewAsGrant> Filter(IQueryable<ViewAsGrant> grants, ViewAsGrantListRequest request, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(request);

        return ByStatus(ByWindow(ByEmail(grants, request), request), request.Status, now);
    }

    private static IQueryable<ViewAsGrant> ByEmail(IQueryable<ViewAsGrant> grants, ViewAsGrantListRequest request)
    {
        if (ViewAsGrant.NormalizeDisplay(request.Staff) is { } staff)
        {
            grants = grants.Where(g => g.StaffDisplay != null && g.StaffDisplay.Contains(staff));
        }

        if (ViewAsGrant.NormalizeDisplay(request.Viewed) is { } viewed)
        {
            grants = grants.Where(g => g.TargetDisplay != null && g.TargetDisplay.Contains(viewed));
        }

        return grants;
    }

    private static IQueryable<ViewAsGrant> ByWindow(IQueryable<ViewAsGrant> grants, ViewAsGrantListRequest request)
    {
        if (request.From is { } from)
        {
            grants = grants.Where(g => g.GrantedAt >= from);
        }

        if (request.To is { } to)
        {
            grants = grants.Where(g => g.GrantedAt < to);
        }

        return grants;
    }

    private static IQueryable<ViewAsGrant> ByStatus(IQueryable<ViewAsGrant> grants, ViewAsGrantStatus? status, DateTime now) =>
        status switch
        {
            ViewAsGrantStatus.Live => grants.Where(g => g.EndedAt == null && g.ExpiresAt > now),
            ViewAsGrantStatus.Ended => grants.Where(g => g.EndedAt != null),
            ViewAsGrantStatus.Expired => grants.Where(g => g.EndedAt == null && g.ExpiresAt <= now),
            _ => grants,
        };
}
