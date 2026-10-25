namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// The installation registry behind FCM push (ADR-044): where ownership lives, since an FCM token
/// carries no owner. <see cref="FirestoreInstallationRegistry"/> is the implementation; the interface
/// exists because <c>FirestoreDb</c> is sealed, so the registrar and sender rules are unit-tested
/// against an in-memory registry while the Firestore one runs against the emulator.
/// </summary>
internal interface IFcmInstallationRegistry
{
    /// <summary>Reads one installation.</summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation, or <see langword="null"/> when the id is unknown.</returns>
    Task<FcmInstallation?> GetAsync(string installationId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an installation atomically under the ownership rule: refused when the id exists with
    /// another owner AND a different push channel (another device's installation); a new id, the
    /// caller's own installation, or one whose channel the caller presents (the same device changing
    /// hands) is written.
    /// </summary>
    /// <param name="installation">The installation, owned by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Whether the write happened, and the push channel the installation held before (or
    /// <see langword="null"/> for a new id), so the caller can move the topic subscription.
    /// </returns>
    Task<(bool Claimed, string? PreviousPushChannel)> TryClaimAsync(FcmInstallation installation, CancellationToken cancellationToken);

    /// <summary>Every installation the given users own.</summary>
    /// <param name="userKeys">The users' stored forms, distinct.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The users' installations.</returns>
    Task<IReadOnlyList<FcmInstallation>> QueryByUsersAsync(IReadOnlyList<string> userKeys, CancellationToken cancellationToken);

    /// <summary>Deletes an installation only while the given user still owns it.</summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="userKey">The owner's stored form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a document was deleted.</returns>
    Task<bool> DeleteOwnedAsync(string installationId, string userKey, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an installation only while it still holds the given token, so a stale-token cleanup
    /// never removes a registration refreshed onto a new token in the meantime.
    /// </summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="pushChannel">The token FCM reported dead.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a document was deleted.</returns>
    Task<bool> DeleteIfPushChannelAsync(string installationId, string pushChannel, CancellationToken cancellationToken);
}
