using System.Globalization;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// One device installation in the Firestore registry: the client's installation id (the document
/// id), its owner, the platform it registered with and the FCM registration token that delivers to it.
/// </summary>
/// <param name="InstallationId">The client-generated installation id, the document id.</param>
/// <param name="UserId">The owning user, in its invariant string form (the queried field).</param>
/// <param name="Platform">The normalized platform value (always <c>fcmv1</c> on this adapter).</param>
/// <param name="PushChannel">The FCM registration token.</param>
internal sealed record FcmInstallation(
    string InstallationId,
    string UserId,
    string Platform,
    string PushChannel)
{
    /// <summary>The invariant string form a user id is stored and queried under.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The stored form.</returns>
    internal static string UserKey(UserIdentifierType userId) =>
        string.Create(CultureInfo.InvariantCulture, $"{userId}");
}
