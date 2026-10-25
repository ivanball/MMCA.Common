using System.Text.RegularExpressions;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Firebase Cloud Messaging push settings, bound from <c>NativePush:Fcm</c> (ADR-044). The shared
/// <c>NativePush:Enabled</c> key is the gate, read by the registration, so one switch means the same
/// thing whichever cloud delivers the push. The Firebase project, the Firestore database and the
/// topic are provisioned outside the application, so the pipeline ships inert and is switched on by
/// configuration alone.
/// </summary>
internal sealed partial class FcmNativePushSettings
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

    /// <summary>
    /// The FCM topic every registered token subscribes to, the target of a broadcast. A bare name
    /// (<c>broadcast</c>) or the <c>/topics/</c> form; FCM allows <c>[a-zA-Z0-9-_.~%]</c>.
    /// </summary>
    public string? BroadcastTopic { get; init; }

    /// <summary>Whether the section names everything a send and a registration need.</summary>
    internal bool IsComplete =>
        !string.IsNullOrWhiteSpace(ProjectId)
        && !string.IsNullOrWhiteSpace(InstallationsCollection)
        && !string.IsNullOrWhiteSpace(BroadcastTopic);

    /// <summary>
    /// Fails when <see cref="BroadcastTopic"/> is not a name FCM accepts, so a typo fails at
    /// registration rather than on every device registration and broadcast.
    /// </summary>
    /// <exception cref="InvalidOperationException">The topic name is invalid (the message names the key).</exception>
    internal void EnsureValidTopic()
    {
        if (!TopicPattern.IsMatch(BroadcastTopic ?? string.Empty))
        {
            throw new InvalidOperationException(
                $"{SectionPath}:{nameof(BroadcastTopic)} '{BroadcastTopic}' is not a valid FCM topic name: use the characters [a-zA-Z0-9-_.~%], optionally prefixed with /topics/.");
        }
    }

    [GeneratedRegex(@"^(/topics/)?[a-zA-Z0-9\-_.~%]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TopicPattern { get; }
}
