using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
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
    IDataSourceResolver dataSourceResolver,
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
    /// <remarks>
    /// The user is part of the predicate, not a check after the fact: the id arrives from a client, so
    /// filtering in the query is what makes another account's session unreadable rather than merely
    /// rejected after being read.
    /// </remarks>
    public async Task<RefreshSession?> FindByIdAsync(
        Guid id,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        await Sessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContextFactory.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The claim is a conditional UPDATE (<c>WHERE Id = @id AND RevokedAt IS NULL</c>) rather than a
    /// tracked mutation, because the tracked read that produced <paramref name="presented"/> is a
    /// check-then-act: two concurrent refreshes of the SAME token each get their own copy with
    /// <c>RevokedAt</c> null and both would save. The row itself carries no concurrency token by
    /// design (<see cref="RefreshSession"/>), so the database arbitrates through the predicate: the
    /// winner affects one row, the loser affects none and writes nothing.
    /// </para>
    /// <para>
    /// The UPDATE and the successor INSERT share one transaction so a loser cannot observe a
    /// half-finished rotation: its UPDATE blocks on the winner's row lock, re-evaluates the
    /// predicate after the winner commits, and the family revocation it then performs sees the
    /// committed successor. A nested call joins the ambient transaction, so an
    /// <c>ITransactional</c> caller is unaffected.
    /// </para>
    /// </remarks>
    public Task<bool> TryRotateAsync(
        RefreshSession presented,
        RefreshSession successor,
        DateTime revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(successor);

        // Resolved BEFORE the transaction opens so this session's context is one of the contexts
        // BeginTransaction enlists, rather than a late arrival.
        var entry = Context.Entry(presented);

        return dbContextFactory.ExecuteInTransactionAsync(
            async ct =>
            {
                var claimed = await Sessions
                    .Where(s => s.Id == presented.Id && s.RevokedAt == null)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(s => s.RevokedAt, revokedAt)
                            .SetProperty(s => s.ReasonRevoked, RefreshSession.ReasonRotated)
                            .SetProperty(s => s.ReplacedByTokenHash, successor.TokenHash),
                        ct)
                    .ConfigureAwait(false) == 1;

                if (!claimed)
                {
                    return false;
                }

                // Mirror the claim onto the tracked instance so callers see a revoked session, then
                // accept those values as original: without it the next SaveChanges would re-issue
                // the same UPDATE as a tracked modification.
                presented.Revoke(revokedAt, RefreshSession.ReasonRotated, successor.TokenHash);
                entry.OriginalValues.SetValues(entry.CurrentValues);

                await Sessions.AddAsync(successor, ct).ConfigureAwait(false);
                await dbContextFactory.SaveChangesAsync(ct).ConfigureAwait(false);

                return true;
            },
            cancellationToken);
    }

    // The configured NAME is used verbatim, as it always was; only the engine goes through the
    // resolver, so a host that configures no SQL Server connection string gets the engine it does
    // configure rather than a context over an empty connection string.
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(RefreshSession).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
