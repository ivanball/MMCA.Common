using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Shared.Notifications.PushNotifications;

/// <summary>
/// Client request to register (or refresh) THIS device installation for native push delivery
/// (ADR-044). The installation id is a client-generated stable identifier (a GUID persisted in
/// device preferences) so re-registering after a token rotation updates the same installation;
/// the push channel is the platform handle (FCM registration token / APNs device token).
/// Ownership is stamped server-side from the authenticated user, never sent by the client.
/// </summary>
public sealed record DeviceInstallationRequest
{
    /// <summary>Platform value for Google (FCM v1) delivery.</summary>
    public const string FcmV1Platform = "fcmv1";

    /// <summary>Platform value for Apple (APNs) delivery.</summary>
    public const string ApnsPlatform = "apns";

    /// <summary>Client-generated stable installation identifier.</summary>
    [Required]
    [MaxLength(128)]
    public required string InstallationId { get; init; }

    /// <summary>Push platform: <see cref="FcmV1Platform"/> or <see cref="ApnsPlatform"/>.</summary>
    [Required]
    [MaxLength(16)]
    public required string Platform { get; init; }

    /// <summary>The platform push handle (FCM registration token / APNs device token).</summary>
    [Required]
    [MaxLength(1024)]
    public required string PushChannel { get; init; }
}
