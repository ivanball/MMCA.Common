using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Persistence for <see cref="RefreshSession"/> rows: the multi-device replacement for the single
/// plaintext refresh-token column the user aggregate used to carry.
/// <para>
/// The contract is deliberately narrow. Sessions are looked up by hash (never by token), listed per
/// user for the cap and for family revocation, and mutated only through
/// <see cref="RefreshSession.Revoke"/> on instances this store returned, so an implementation that
/// tracks its entities (the shipped EF one) persists a revocation with no update method at all.
/// <see cref="TryRotateAsync"/> is the one exception: rotation is a claim, not a mutation, so it
/// needs a write the store itself decides the outcome of.
/// </para>
/// <para>
/// Implementations must return <b>tracked</b> instances from every lookup except
/// <see cref="FindByIdUntrackedAsync"/>, which is untracked by design and for reading only: a
/// no-tracking read elsewhere would take revocations and rotations and drop them silently at save time.
/// </para>
/// </summary>
public interface IRefreshSessionStore
{
    /// <summary>Stages a new session for insertion.</summary>
    /// <param name="session">The session to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the session whose token hash matches, revoked and expired rows included. Returning
    /// revoked rows is load-bearing: a rotated token that comes back is found on its revoked row,
    /// which is the reuse signal (BR-206). A store that filtered them out would report replay as
    /// "unknown token" and never revoke the family.
    /// </summary>
    /// <param name="tokenHash">The hash produced by <see cref="RefreshSession.HashToken"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or null when no row carries that hash.</returns>
    Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user's un-revoked sessions (expired ones included, since they still occupy a row),
    /// oldest first, so the caller can revoke a family or evict past the per-user cap deterministically.
    /// </summary>
    /// <param name="userId">The user whose sessions to list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RefreshSession>> GetUnrevokedByUserAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct users holding at least one LIVE session at <paramref name="now"/>: not
    /// revoked and <c>ExpiresAt</c> strictly after <paramref name="now"/>
    /// (<see cref="RefreshSession.IsActiveAt"/>). One query, evaluated in the store.
    /// </summary>
    /// <param name="now">The UTC instant that decides which sessions have expired.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each such user exactly once, in no particular order.</returns>
    Task<IReadOnlyList<UserIdentifierType>> GetUserIdsWithLiveSessionsAsync(
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the LIVE sessions (as <see cref="GetUserIdsWithLiveSessionsAsync"/> defines them) of each
    /// of <paramref name="userIds"/> in one grouped query, never one query per user. A requested user
    /// with no live session is absent from the result.
    /// </summary>
    /// <param name="userIds">The users to count; duplicates count once.</param>
    /// <param name="now">The UTC instant that decides which sessions have expired.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live-session count per requested user that has at least one.</returns>
    Task<IReadOnlyDictionary<UserIdentifierType, int>> CountLiveSessionsByUserAsync(
        IReadOnlyCollection<UserIdentifierType> userIds,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds one of <paramref name="userId"/>'s sessions by its identifier, revoked and expired rows
    /// included. The user is part of the lookup rather than a check the caller does afterwards: a
    /// session id is a value a client hands back, so scoping the query to the owner is what makes
    /// another account's id indistinguishable from a nonexistent one.
    /// </summary>
    /// <param name="id">The session identifier (the token's <c>sid</c> claim, or a row from a device list).</param>
    /// <param name="userId">The user the session must belong to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked session, or null when no such row belongs to that user.</returns>
    Task<RefreshSession?> FindByIdAsync(
        Guid id,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads a session by its identifier as the database holds it now, bypassing any tracked copy,
    /// revoked and expired rows included. The rotation loser needs this: its tracked instance was read
    /// before the winner's conditional update and still shows the row live, so only a fresh read can
    /// tell when (and why) the row was revoked. The returned instance is for reading only; it is not
    /// tracked, so a mutation on it is never persisted.
    /// <para>
    /// The default returns <see langword="null"/>, which the caller treats as "cannot tell" and answers
    /// with the conservative BR-206 family revocation. The shipped EF store overrides it with a
    /// no-tracking query.
    /// </para>
    /// </summary>
    /// <param name="id">The session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fresh, untracked copy of the session, or null when no such row exists.</returns>
    Task<RefreshSession?> FindByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<RefreshSession?>(null);

    /// <summary>Persists staged inserts and revocations.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims <paramref name="presented"/> for rotation (revoking it as
    /// <see cref="RefreshSession.ReasonRotated"/> and linking it to
    /// <paramref name="successor"/>), then stages and persists the successor.
    /// <para>
    /// The return value is the whole point: two requests presenting the SAME still-live token both
    /// read an un-revoked row, so a check-then-act rotation mints two successors from one token and
    /// the presented row can never fire reuse detection again (BR-206). Returning
    /// <see langword="false"/> tells the caller it lost that claim; the caller then re-reads the row
    /// (<see cref="FindByIdUntrackedAsync"/>) to tell a sibling rotation inside the reuse grace (a 409
    /// with nothing revoked) or a sign-out or cap eviction (that request fails alone) from a replay
    /// (the family is revoked).
    /// </para>
    /// <para>
    /// The default implementation is the shape the interface always had (revoke in memory, add,
    /// save). It is atomic only per instance, which is all an in-memory or test store can offer;
    /// the shipped EF store overrides it with a conditional UPDATE the database arbitrates.
    /// </para>
    /// </summary>
    /// <param name="presented">The tracked session behind the presented refresh token.</param>
    /// <param name="successor">The freshly minted session to persist in its place.</param>
    /// <param name="revokedAt">The UTC instant to record on the presented session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this caller claimed the rotation and the successor is persisted;
    /// <see langword="false"/> when the presented session was already revoked, in which case
    /// nothing was written.
    /// </returns>
    async Task<bool> TryRotateAsync(
        RefreshSession presented,
        RefreshSession successor,
        DateTime revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(successor);

        if (presented.Revoke(revokedAt, RefreshSession.ReasonRotated, successor.TokenHash).IsFailure)
        {
            return false;
        }

        await AddAsync(successor, cancellationToken).ConfigureAwait(false);
        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
