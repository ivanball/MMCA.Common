using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// EF Core <see cref="IViewAsGrantStore"/> over the consumer's Identity database (ADR-137).
/// <para>
/// <b>Which database.</b> Resolved like <see cref="EFPermissionGrantStore"/>: the data-source registry
/// first, falling back to the source named by <c>ViewAs:DataSourceName</c>.
/// </para>
/// <para>
/// <b>Inert while disabled.</b> With <c>ViewAs:Enabled</c> false, or on a service that does not hold
/// the grant store (<c>ViewAs:HostsGrantStore</c> false), the table is in no model, so every read
/// answers as if no grant existed instead of querying a table that is not there. That is what lets
/// the sign-in history read through this store, and the controller actions bind the view-as service,
/// on every host.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the Identity context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound view-as settings.</param>
internal sealed class EFViewAsGrantStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<ViewAsSettings> settings) : IViewAsGrantStore
{
    /// <summary>The model gate's two settings: the table is mapped only when both hold.</summary>
    private bool Enabled => settings.Value.Enabled && settings.Value.HostsGrantStore;

    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<ViewAsGrant> Grants => Context.Set<ViewAsGrant>();

    /// <inheritdoc />
    public async Task AddAsync(ViewAsGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        RequireEnabled();

        await Grants.AddAsync(grant, cancellationToken).ConfigureAwait(false);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ViewAsGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken = default) =>
        Enabled
            ? await Grants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken).ConfigureAwait(false)
            : null;

    /// <inheritdoc />
    public async Task<bool> EndAsync(Guid grantId, UserIdentifierType staffUserId, DateTime endedAt, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return false;
        }

        var grant = await Grants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.StaffUserId == staffUserId, cancellationToken)
            .ConfigureAwait(false);
        if (grant is null)
        {
            return false;
        }

        if (grant.EndedAt is null)
        {
            grant.End(endedAt);
            await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ViewAsGrant>> GetRecentForTargetAsync(
        UserIdentifierType targetUserId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled || maxCount <= 0)
        {
            return [];
        }

        return await Grants
            .AsNoTracking()
            .Where(g => g.TargetUserId == targetUserId)
            .OrderByDescending(g => g.GrantedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteIssuedBeforeAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        Enabled
            ? await Grants.Where(g => g.GrantedAt < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false)
            : 0;

    /// <inheritdoc />
    /// <remarks>
    /// Two statements, a count and the page, both ordered on <c>GrantedAt</c> (the
    /// <c>IX_ViewAsGrants_GrantedAt</c> index the retention sweep already uses) with the id as the
    /// tie-break, so equal instants cannot repeat or skip a row across pages. The email needles are
    /// folded by <see cref="ViewAsGrant.NormalizeDisplay"/>, the form the snapshots are stored in, so
    /// the match does not depend on the database collation.
    /// </remarks>
    public async Task<(IReadOnlyList<ViewAsGrant> Items, int TotalCount)> ListAsync(
        ViewAsGrantListRequest request,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enabled)
        {
            return ([], 0);
        }

        var filtered = ViewAsGrantLogQuery.Filter(Grants.AsNoTracking(), request, now);
        var (skip, take) = PagingMath.Clamp(request.PageNumber, request.PageSize, ViewAsGrantListRequest.MaxPageSize);

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await filtered
            .OrderByDescending(g => g.GrantedAt)
            .ThenByDescending(g => g.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<int> ForgetUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return 0;
        }

        // Tracked on purpose: the erasure's own unit-of-work save persists the blanking, so it
        // commits (or rolls back) with the rest of the erasure.
        var grants = await Grants
            .Where(g => g.StaffUserId == userId && g.StaffDisplay != null || g.TargetUserId == userId && g.TargetDisplay != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return grants.Count(g => g.ForgetDisplayOf(userId));
    }

    private void RequireEnabled()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("View-as grants cannot be written while ViewAs:Enabled or ViewAs:HostsGrantStore is false: the table is not mapped.");
        }
    }

    // The configured NAME is used verbatim; only the engine goes through the resolver (the
    // EFPermissionGrantStore precedent).
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(ViewAsGrant).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
