using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// SNS mobile push settings, bound from <c>NativePush:Sns</c> (ADR-044). The shared
/// <c>NativePush:Enabled</c> key is the gate, read by the registration, so one switch means the same
/// thing whichever cloud delivers the push. Every resource named here is provisioned outside the
/// application (platform applications with their FCM / APNs credentials, the topic, the table), so
/// the pipeline ships inert and is switched on by configuration alone.
/// </summary>
internal sealed class SnsNativePushSettings
{
    /// <summary>The configuration section path, also named in configuration errors.</summary>
    internal const string SectionPath = "NativePush:Sns";

    /// <summary>The shared gate, a sibling of this section.</summary>
    internal const string EnabledKey = "NativePush:Enabled";

    /// <summary>The ARN of the SNS platform application for FCM v1 (Android) devices.</summary>
    public string? FcmPlatformApplicationArn { get; init; }

    /// <summary>The ARN of the SNS platform application for APNs (iOS) devices, production or sandbox.</summary>
    public string? ApnsPlatformApplicationArn { get; init; }

    /// <summary>The SNS topic every registered endpoint subscribes to, the target of a broadcast.</summary>
    public string? BroadcastTopicArn { get; init; }

    /// <summary>The DynamoDB table holding one item per device installation.</summary>
    public string? InstallationsTable { get; init; }

    /// <summary>
    /// The region of the SNS and DynamoDB resources (system name, e.g. <c>us-east-1</c>). With
    /// <see cref="ServiceUrl"/> it is the signing region; when blank the AWS SDK region chain applies.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>An endpoint override for both SNS and DynamoDB, e.g. LocalStack.</summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>
    /// Optional static access key, set together with <see cref="SecretAccessKey"/>; when both are blank
    /// the AWS default credential chain applies. Setting only one of the two fails registration.
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>The secret paired with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// Whether the section names everything a send and a registration need: the table, the topic and
    /// at least one platform application.
    /// </summary>
    internal bool IsComplete =>
        !string.IsNullOrWhiteSpace(InstallationsTable)
        && !string.IsNullOrWhiteSpace(BroadcastTopicArn)
        && (!string.IsNullOrWhiteSpace(FcmPlatformApplicationArn) || !string.IsNullOrWhiteSpace(ApnsPlatformApplicationArn));

    /// <summary>The platform application for a normalized platform value, or <see langword="null"/> when not configured.</summary>
    /// <param name="platform"><see cref="DeviceInstallationRequest.FcmV1Platform"/> or <see cref="DeviceInstallationRequest.ApnsPlatform"/>.</param>
    /// <returns>The platform application ARN, or <see langword="null"/>.</returns>
    internal string? PlatformApplicationArnFor(string platform)
    {
        var arn = platform switch
        {
            DeviceInstallationRequest.FcmV1Platform => FcmPlatformApplicationArn,
            DeviceInstallationRequest.ApnsPlatform => ApnsPlatformApplicationArn,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(arn) ? null : arn;
    }
}
