using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// An in-memory <see cref="IViewAsGrantStore"/> for view-as workflow tests (ADR-137), mirroring the EF
/// implementation: the same instances on every read (ending a grant mutates what the store holds), and
/// the staff user is part of the end lookup.
/// </summary>
public sealed class InMemoryViewAsGrantStore : IViewAsGrantStore
{
    private readonly List<ViewAsGrant> _grants = [];

    /// <summary>Gets every stored grant, in insertion order.</summary>
    public IReadOnlyList<ViewAsGrant> Grants => _grants;

    /// <summary>Places an already-persisted grant in the store.</summary>
    /// <param name="grant">The grant to seed.</param>
    public void Seed(ViewAsGrant grant) => _grants.Add(grant);

    /// <inheritdoc />
    public Task AddAsync(ViewAsGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grants.Add(grant);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ViewAsGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_grants.Find(g => g.Id == grantId));

    /// <inheritdoc />
    public Task<bool> EndAsync(Guid grantId, UserIdentifierType staffUserId, DateTime endedAt, CancellationToken cancellationToken = default)
    {
        var grant = _grants.Find(g => g.Id == grantId && g.StaffUserId == staffUserId);
        grant?.End(endedAt);
        return Task.FromResult(grant is not null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ViewAsGrant>> GetRecentForTargetAsync(
        UserIdentifierType targetUserId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ViewAsGrant> recent =
        [
            .. _grants
                .Where(g => g.TargetUserId == targetUserId)
                .OrderByDescending(g => g.GrantedAt)
                .Take(maxCount),
        ];
        return Task.FromResult(recent);
    }

    /// <inheritdoc />
    public Task<int> DeleteIssuedBeforeAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        Task.FromResult(_grants.RemoveAll(g => g.GrantedAt < cutoff));

    /// <inheritdoc />
    /// <remarks>The same filters as the EF store (<see cref="ViewAsGrantLogQuery"/>), ordering and clamp.</remarks>
    public Task<(IReadOnlyList<ViewAsGrant> Items, int TotalCount)> ListAsync(
        ViewAsGrantListRequest request,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var filtered = ViewAsGrantLogQuery.Filter(_grants.AsQueryable(), request, now).ToList();
        var (skip, take) = PagingMath.Clamp(request.PageNumber, request.PageSize, ViewAsGrantListRequest.MaxPageSize);
        IReadOnlyList<ViewAsGrant> page =
        [
            .. filtered
                .OrderByDescending(g => g.GrantedAt)
                .ThenByDescending(g => g.Id)
                .Skip(skip)
                .Take(take),
        ];

        return Task.FromResult((page, filtered.Count));
    }

    /// <inheritdoc />
    /// <remarks>Mutates the held instances, as the EF store mutates tracked rows.</remarks>
    public Task<int> ForgetUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_grants.Count(g => g.ForgetDisplayOf(userId)));
}
