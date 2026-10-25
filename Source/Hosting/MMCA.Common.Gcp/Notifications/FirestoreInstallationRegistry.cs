using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// The Firestore installation registry behind FCM push (ADR-044). An FCM token has no owner of its
/// own (Notification Hubs keeps the owner as an installation tag), so this collection holds it: one
/// document per installation id with the owner, platform and token the ownership rule compares.
/// Every ownership decision is a Firestore transaction (read, decide, write), so it holds under
/// concurrency without a lock: a concurrent change to the document makes Firestore retry the whole
/// decision against the new state.
/// </summary>
/// <param name="firestore">The Firestore database.</param>
/// <param name="options">The push settings (the collection name).</param>
internal sealed class FirestoreInstallationRegistry(FirestoreDb firestore, IOptions<FcmNativePushSettings> options) : IFcmInstallationRegistry
{
    /// <summary>
    /// Firestore's limit on the values of one WhereIn filter: user-targeted sends query at most
    /// this many users at a time. <c>UserId</c> equality is served by the automatic single-field
    /// index, so the collection needs no composite index.
    /// </summary>
    internal const int MaxUsersPerQuery = 30;

    internal const string UserIdField = "UserId";
    internal const string PlatformField = "Platform";
    internal const string PushChannelField = "PushChannel";
    internal const string UpdatedAtField = "UpdatedAt";

    private CollectionReference Collection => firestore.Collection(options.Value.InstallationsCollection);

    /// <inheritdoc />
    public async Task<FcmInstallation?> GetAsync(string installationId, CancellationToken cancellationToken)
    {
        var snapshot = await Collection.Document(installationId).GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return FromSnapshot(snapshot);
    }

    /// <inheritdoc />
    public Task<(bool Claimed, string? PreviousPushChannel)> TryClaimAsync(FcmInstallation installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var document = Collection.Document(installation.InstallationId);
        return firestore.RunTransactionAsync(
            async transaction =>
            {
                var existing = FromSnapshot(await transaction.GetSnapshotAsync(document, cancellationToken).ConfigureAwait(false));
                if (existing is not null
                    && !string.Equals(existing.UserId, installation.UserId, StringComparison.Ordinal)
                    && !string.Equals(existing.PushChannel, installation.PushChannel, StringComparison.Ordinal))
                {
                    return (false, existing.PushChannel);
                }

                transaction.Set(document, new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [UserIdField] = installation.UserId,
                    [PlatformField] = installation.Platform,
                    [PushChannelField] = installation.PushChannel,
                    [UpdatedAtField] = FieldValue.ServerTimestamp,
                });
                return (true, existing?.PushChannel);
            },
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FcmInstallation>> QueryByUsersAsync(IReadOnlyList<string> userKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userKeys);

        List<FcmInstallation> installations = [];
        foreach (var batch in userKeys.Chunk(MaxUsersPerQuery))
        {
            var snapshot = await Collection.WhereIn(UserIdField, batch).GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            foreach (var document in snapshot.Documents)
            {
                if (FromSnapshot(document) is { } installation)
                {
                    installations.Add(installation);
                }
            }
        }

        return installations;
    }

    /// <inheritdoc />
    public Task<bool> DeleteOwnedAsync(string installationId, string userKey, CancellationToken cancellationToken) =>
        DeleteWhenAsync(installationId, existing => string.Equals(existing.UserId, userKey, StringComparison.Ordinal), cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteIfPushChannelAsync(string installationId, string pushChannel, CancellationToken cancellationToken) =>
        DeleteWhenAsync(installationId, existing => string.Equals(existing.PushChannel, pushChannel, StringComparison.Ordinal), cancellationToken);

    private static FcmInstallation? FromSnapshot(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists
            || !snapshot.TryGetValue<string>(UserIdField, out var userId)
            || !snapshot.TryGetValue<string>(PushChannelField, out var pushChannel)
            || string.IsNullOrEmpty(userId)
            || string.IsNullOrEmpty(pushChannel))
        {
            // Unknown, or not a document this registry wrote: nothing to deliver to or decide on.
            return null;
        }

        return new FcmInstallation(
            snapshot.Id,
            userId,
            snapshot.TryGetValue<string>(PlatformField, out var platform) && platform is not null ? platform : string.Empty,
            pushChannel);
    }

    private Task<bool> DeleteWhenAsync(string installationId, Func<FcmInstallation, bool> condition, CancellationToken cancellationToken)
    {
        var document = Collection.Document(installationId);
        return firestore.RunTransactionAsync(
            async transaction =>
            {
                var existing = FromSnapshot(await transaction.GetSnapshotAsync(document, cancellationToken).ConfigureAwait(false));
                if (existing is null || !condition(existing))
                {
                    // Gone, or it changed hands or tokens since it was read: no longer this delete's to make.
                    return false;
                }

                transaction.Delete(document);
                return true;
            },
            cancellationToken: cancellationToken);
    }
}
