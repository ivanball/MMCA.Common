using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// EF Core <see cref="IRefreshSessionStore"/> over the consumer's Identity database.
/// <para>
/// <b>Which database.</b> The table is opted into one model
/// (<see cref="RefreshSessionModelBuilderExtensions.ApplyRefreshSessionConfiguration"/>), so this
/// resolves the physical source the same way the rest of the framework routes an entity: the
/// data-source registry first (a consumer that ships a real entity configuration for
/// <see cref="RefreshSession"/> is routed by it like any other entity), falling back to the source
/// named by <c>RefreshSessions:DataSourceName</c>, which defaults to the engine's Default source and
/// so is exactly right for a single-database host. Pointing it at a database that does not map the
/// table fails loudly on the first query rather than reading the wrong rows.
/// </para>
/// <para>
/// <b>Tracked reads.</b> Every read here is tracked on purpose: the caller revokes by mutating the
/// instances this returns, and a no-tracking query would take those revocations and silently drop
/// them at save time.
/// </para>
/// </summary>
internal sealed class EFRefreshSessionStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IOptions<RefreshSessionSettings> settings) : IRefreshSessionStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<RefreshSession> Sessions => Context.Set<RefreshSession>();

    /// <inheritdoc />
    public async Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await Sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return await Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ordered by <c>CreatedAt</c> with the key as tie-break: the cap evicts "the oldest", and two
    /// sessions opened in the same clock tick would otherwise evict in an arbitrary order.
    /// </remarks>
    public async Task<IReadOnlyList<RefreshSession>> GetUnrevokedByUserAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        await Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContextFactory.SaveChangesAsync(cancellationToken);

    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(RefreshSession).FullName!, out var key)
            ? key
            : new DataSourceKey(DataSource.SQLServer, settings.Value.DataSourceName);
}
