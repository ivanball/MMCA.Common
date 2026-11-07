namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging push settings, bound from <c>NativePush:Fcm</c> (ADR-044). The shared
/// <c>NativePush:Enabled</c> key is the gate, read by the registration, so one switch means the same
/// thing whichever cloud delivers the push. The Firebase project and the Firestore database are
/// provisioned outside the application, so the pipeline ships inert and is switched on by
/// configuration alone.
/// </summary>
internal sealed class FcmNativePushSettings
{
    /// <summary>The configuration section path, also named in configuration errors.</summary>
    internal const string SectionPath = "NativePush:Fcm";

    /// <summary>The shared gate, a sibling of this section.</summary>
    internal const string EnabledKey = "NativePush:Enabled";

    /// <summary>
    /// The Google Cloud project that owns both the Firebase app (the FCM HTTP v1 sender) and the
    /// Firestore database holding the installation registry.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>The Firestore collection holding one document per device installation.</summary>
    public string? InstallationsCollection { get; init; }

    /// <summary>Whether the section names everything a send and a registration need.</summary>
    internal bool IsComplete =>
        !string.IsNullOrWhiteSpace(ProjectId)
        && !string.IsNullOrWhiteSpace(InstallationsCollection);
}
