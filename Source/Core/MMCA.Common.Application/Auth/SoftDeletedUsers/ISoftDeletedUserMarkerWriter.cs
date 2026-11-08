namespace MMCA.Common.Application.Auth.SoftDeletedUsers;

/// <summary>
/// Writes the soft-deleted user marker (ADR-047) for as long as an already-issued access token on
/// this host can still be accepted, so a workflow that erases an account needs neither the cache nor
/// the token settings itself.
/// </summary>
public interface ISoftDeletedUserMarkerWriter
{
    /// <summary>Marks the user as soft-deleted, so requests bearing an already-issued token are rejected.</summary>
    /// <param name="userId">The user identifier that was soft-deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the marker has been written.</returns>
    Task MarkDeletedAsync(UserIdentifierType userId, CancellationToken cancellationToken);
}
