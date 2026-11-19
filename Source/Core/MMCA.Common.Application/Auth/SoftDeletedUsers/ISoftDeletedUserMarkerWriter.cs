namespace MMCA.Common.Application.Auth.SoftDeletedUsers;

/// <summary>
/// Writes and clears the soft-deleted user marker (ADR-047), which now means "deleted or locked": an
/// account erasure and an administrator's lock both write it, for as long as an already-issued access
/// token on this host can still be accepted, so neither workflow needs the cache nor the token settings
/// itself. An unlock clears it.
/// </summary>
public interface ISoftDeletedUserMarkerWriter
{
    /// <summary>
    /// Marks the user as deleted or locked, so requests bearing an already-issued token are rejected.
    /// </summary>
    /// <param name="userId">The user identifier that was soft-deleted or locked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the marker has been written.</returns>
    Task MarkDeletedAsync(UserIdentifierType userId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the user's marker from the shared store, so a token issued after an unlock is accepted
    /// at once on every host instead of being refused until the marker expires.
    /// </summary>
    /// <param name="userId">The user identifier that was unlocked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the marker has been removed.</returns>
    Task ClearMarkerAsync(UserIdentifierType userId, CancellationToken cancellationToken);
}
