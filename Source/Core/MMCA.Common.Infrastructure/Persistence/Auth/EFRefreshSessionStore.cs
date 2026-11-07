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
/// <b>Tracked reads.</b> Every read that returns <see cref="RefreshSession"/> entities is tracked on
/// purpose: the caller revokes by mutating the instances this returns, and a no-tracking query would
/// take those revocations and silently drop them at save time. The one entity exception is
/// <see cref="FindByIdUntrackedAsync"/>, a read-only re-read for the rotation loser, and
/// <see cref="GetRecentSignInsAsync"/>, a read-only sign-in history page. The scalar
/// projections (<see cref="GetUserIdsWithLiveSessionsAsync"/>, <see cref="CountLiveSessionsByUserAsync"/>)
/// return no entities, so nothing is tracked there either.
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
    public Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);
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
    /// <remarks>Tracked, like every read here: erasure revokes and anonymizes the returned rows in place.</remarks>
    public async Task<IReadOnlyList<RefreshSession>> GetAllByUserAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        await Sessions
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// At most three statements, each bounded by <paramref name="maxSignIns"/> and none by the user's
    /// row count: <see cref="SignInChainsQuery"/> groups the user's rows by <c>SignInId</c> inside the
    /// database (MIN and MAX of <c>CreatedAt</c>, newest groups only), <see cref="SignInHeadsQuery"/>
    /// loads only the row at each group's MAX, and <see cref="LegacySignInsQuery"/> reads the capped
    /// un-rotated rows that predate the column. The <c>(UserId, SignInId, CreatedAt)</c> index covers
    /// the first two. All are untracked: a history read never revokes.
    /// </remarks>
    public async Task<IReadOnlyList<RefreshSessionSignIn>> GetRecentSignInsAsync(
        UserIdentifierType userId,
        int maxSignIns,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSignIns);

        var sessions = Sessions.AsNoTracking();

        var chains = await SignInChainsQuery(sessions, userId, maxSignIns)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<RefreshSessionSignIn> signIns = [];
        if (chains.Count > 0)
        {
            var heads = await SignInHeadsQuery(
                    sessions,
                    userId,
                    [.. chains.Select(c => c.SignInId)],
                    [.. chains.Select(c => c.LastActiveAt).Distinct()])
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var chain in chains)
            {
                // The IN lists can pair one chain's id with another chain's instant, and two rows of one
                // chain can share its newest instant, so the head is matched exactly here with the key
                // as the tie-break: one head per sign-in.
                var head = heads
                    .Where(h => h.SignInId == chain.SignInId && h.CreatedAt == chain.LastActiveAt)
                    .MaxBy(h => h.Id);
                if (head is not null)
                {
                    signIns.Add(new RefreshSessionSignIn(chain.SignInId.GetValueOrDefault(), chain.StartedAt, head));
                }
            }
        }

        var legacy = await LegacySignInsQuery(sessions, userId, maxSignIns)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. signIns
                .Concat(legacy.Select(row => new RefreshSessionSignIn(row.Id, row.CreatedAt, row)))
                .OrderByDescending(s => s.StartedAt)
                .ThenByDescending(s => s.SignInId)
                .Take(maxSignIns),
        ];
    }

    /// <summary>
    /// The newest <paramref name="maxSignIns"/> sign-ins of one user, newest first by start, as scalars
    /// only: each <c>SignInId</c> with the MIN and MAX of its rows' <c>CreatedAt</c>, grouped, ordered
    /// and limited in the database. No entity is materialized.
    /// </summary>
    /// <param name="sessions">The session set to query.</param>
    /// <param name="userId">The user whose sign-ins to read.</param>
    /// <param name="maxSignIns">The most sign-ins to return.</param>
    /// <returns>The query; not yet executed.</returns>
    internal static IQueryable<SignInChainRow> SignInChainsQuery(
        IQueryable<RefreshSession> sessions,
        UserIdentifierType userId,
        int maxSignIns) =>
        sessions
            .Where(s => s.UserId == userId && s.SignInId != null)
            .GroupBy(s => s.SignInId)
            .OrderByDescending(g => g.Min(s => s.CreatedAt))
            .ThenByDescending(g => g.Key)
            .Take(maxSignIns)
            .Select(g => new SignInChainRow
            {
                SignInId = g.Key,
                StartedAt = g.Min(s => s.CreatedAt),
                LastActiveAt = g.Max(s => s.CreatedAt),
            });

    /// <summary>
    /// The newest row of each listed sign-in: rows of the user whose <c>SignInId</c> is one of
    /// <paramref name="signInIds"/> and whose <c>CreatedAt</c> is one of their newest instants. A seek
    /// per sign-in on the <c>(UserId, SignInId, CreatedAt)</c> index; it materializes one row per
    /// sign-in, plus only the rare row that shares another listed sign-in's newest instant.
    /// </summary>
    /// <param name="sessions">The session set to query.</param>
    /// <param name="userId">The user whose rows to read.</param>
    /// <param name="signInIds">The sign-ins whose heads to load.</param>
    /// <param name="lastActiveAts">Those sign-ins' newest issue instants.</param>
    /// <returns>The query; not yet executed.</returns>
    internal static IQueryable<RefreshSession> SignInHeadsQuery(
        IQueryable<RefreshSession> sessions,
        UserIdentifierType userId,
        IReadOnlyCollection<Guid?> signInIds,
        IReadOnlyCollection<DateTime> lastActiveAts) =>
        sessions.Where(s => s.UserId == userId
            && signInIds.Contains(s.SignInId)
            && lastActiveAts.Contains(s.CreatedAt));

    /// <summary>
    /// The user's newest <paramref name="maxSignIns"/> rows written before <c>SignInId</c> existed that
    /// were not rotated away: each one is its own sign-in (a rotated one is represented by its
    /// successor). The predicate leads with <c>(UserId, SignInId IS NULL)</c>, the same index.
    /// </summary>
    /// <param name="sessions">The session set to query.</param>
    /// <param name="userId">The user whose legacy rows to read.</param>
    /// <param name="maxSignIns">The most rows to return.</param>
    /// <returns>The query; not yet executed.</returns>
    internal static IQueryable<RefreshSession> LegacySignInsQuery(
        IQueryable<RefreshSession> sessions,
        UserIdentifierType userId,
        int maxSignIns) =>
        sessions
            .Where(s => s.UserId == userId
                && s.SignInId == null
                && (s.ReasonRevoked == null || s.ReasonRevoked != RefreshSession.ReasonRotated))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(maxSignIns);

    /// <inheritdoc />
    /// <remarks>
    /// A scalar projection, so nothing is tracked: no caller revokes through these rows. The live
    /// predicate is <see cref="RefreshSession.IsActiveAt"/> spelled out so it translates to SQL.
    /// </remarks>
    public async Task<IReadOnlyList<UserIdentifierType>> GetUserIdsWithLiveSessionsAsync(
        DateTime now,
        CancellationToken cancellationToken = default) =>
        await Sessions
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// One grouped query over a <c>Contains</c> of the distinct requested ids, so a duplicate id
    /// cannot double a count; an empty request answers empty without touching the database.
    /// </remarks>
    public async Task<IReadOnlyDictionary<UserIdentifierType, int>> CountLiveSessionsByUserAsync(
        IReadOnlyCollection<UserIdentifierType> userIds,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        UserIdentifierType[] ids = [.. userIds.Distinct()];
        if (ids.Length == 0)
        {
            return new Dictionary<UserIdentifierType, int>();
        }

        return await Sessions
            .Where(s => ids.Contains(s.UserId) && s.RevokedAt == null && s.ExpiresAt > now)
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The user is part of the predicate, not a check after the fact: the id arrives from a client, so
    /// filtering in the query is what makes another account's session unreadable rather than merely
    /// rejected after being read.
    /// </remarks>
    public Task<RefreshSession?> FindByIdAsync(
        Guid id,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        Sessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The one no-tracking ENTITY read in this store (the scalar projections track nothing either), and
    /// deliberately so: it exists to see past the tracked
    /// copy (which the identity map would otherwise hand back unchanged) to the row the database holds
    /// after a concurrent rotation. Nothing revokes through the instance it returns.
    /// </remarks>
    public Task<RefreshSession?> FindByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default) =>
        Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

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

    /// <summary>One row of <see cref="SignInChainsQuery"/>: a sign-in and the span of its rows.</summary>
    internal sealed class SignInChainRow
    {
        /// <summary>Gets the sign-in's identifier (never null here; nullable because the column is).</summary>
        public Guid? SignInId { get; init; }

        /// <summary>Gets the issue instant of the sign-in's earliest retained session.</summary>
        public DateTime StartedAt { get; init; }

        /// <summary>Gets the issue instant of the sign-in's newest session.</summary>
        public DateTime LastActiveAt { get; init; }
    }
}
