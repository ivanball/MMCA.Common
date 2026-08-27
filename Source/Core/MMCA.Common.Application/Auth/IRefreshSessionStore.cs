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
/// </para>
/// <para>
/// Implementations must return <b>tracked</b> instances: a no-tracking read would take revocations and
/// rotations and drop them silently at save time.
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

    /// <summary>Persists staged inserts and revocations.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
