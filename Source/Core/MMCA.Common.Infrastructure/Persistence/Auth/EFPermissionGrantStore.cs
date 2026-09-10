using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// EF Core <see cref="IPermissionGrantStore"/> over the consumer's Identity database.
/// <para>
/// <b>Which database.</b> The table is opted into one model
/// (<see cref="PermissionGrantModelBuilderExtensions.ApplyPermissionGrantConfiguration"/>), so this
/// resolves the physical source the way <see cref="EFRefreshSessionStore"/> does: the data-source
/// registry first, falling back to the source named by
/// <c>Authentication:PermissionGrants:DataSourceName</c>, which defaults to the engine's Default
/// source.
/// </para>
/// <para>
/// <b>Reads are untracked.</b> Nothing here mutates a loaded row: a grant is inserted or deleted,
/// never edited, so tracking would only cost the change detector work on the snapshot load that runs
/// on the cache-refresh interval.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the Identity context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound permission-grant settings.</param>
/// <param name="timeProvider">Clock stamping <see cref="PermissionGrant.GrantedAt"/>.</param>
internal sealed class EFPermissionGrantStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<PermissionGrantSettings> settings,
    TimeProvider timeProvider) : IPermissionGrantStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<PermissionGrant> Grants => Context.Set<PermissionGrant>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionGrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await Grants
            .AsNoTracking()
            .OrderBy(g => g.Role)
            .ThenBy(g => g.Permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        return await Grants
            .AsNoTracking()
            .Where(g => g.Role == role)
            .OrderBy(g => g.Permission)
            .Select(g => g.Permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> GrantAsync(
        string role,
        string permission,
        string? grantedBy = null,
        CancellationToken cancellationToken = default)
    {
        var grantResult = PermissionGrant.Create(
            role,
            permission,
            timeProvider.GetUtcNow().UtcDateTime,
            grantedBy);

        if (grantResult.IsFailure)
        {
            return Result.Failure(grantResult.Errors);
        }

        var grant = grantResult.Value!;

        // Check-then-act, and the unique index is what makes losing the race harmless: a concurrent
        // duplicate insert fails at the database, and the row the winner wrote says exactly what the
        // loser was trying to say. The check exists to make the ordinary duplicate free rather than
        // to be the guarantee.
        var exists = await Grants
            .AsNoTracking()
            .AnyAsync(g => g.Role == grant.Role && g.Permission == grant.Permission, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return Result.Success();
        }

        await Grants.AddAsync(grant, cancellationToken).ConfigureAwait(false);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(
        string role,
        string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        // A set-based delete rather than load-then-remove: there is at most one row by the unique
        // index, nothing about it needs to be read, and removing zero rows is the success an
        // idempotent revoke promises.
        await Grants
            .Where(g => g.Role == role && g.Permission == permission)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    // The configured NAME is used verbatim; only the engine goes through the resolver, so a host that
    // configures no SQL Server connection string gets the engine it does configure rather than a
    // context over an empty connection string.
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(PermissionGrant).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
