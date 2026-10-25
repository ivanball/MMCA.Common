using FirebaseAdmin.Messaging;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Builds the FCM HTTP v1 message FirebaseAdmin sends. FirebaseAdmin takes a typed
/// <see cref="Message"/>, not raw JSON, so this maps the same fields the core
/// <see cref="NativePushPayloads"/> builders write (the unit tests pin the equivalence): the
/// <c>notification</c> block and the metadata as <c>data</c> keys, the payload an Android device
/// receives, plus an <c>apns</c> block with the <c>aps.alert</c> title and body and the metadata as
/// top-level custom keys (never <c>aps</c>), the payload FCM forwards to an iOS device that
/// registered an FCM token. One notification therefore looks the same on a device whichever cloud
/// delivered it.
/// </summary>
internal static class FcmPushMessage
{
    /// <summary>The APNs key the alert block lives under, which metadata must not clobber.</summary>
    private const string ApsKey = "aps";

    /// <summary>Builds the message for one registration token.</summary>
    /// <param name="token">The FCM registration token.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata carried in both platform payloads.</param>
    /// <returns>The message.</returns>
    internal static Message ForToken(string token, string title, string body, IReadOnlyDictionary<string, string>? metadata)
    {
        var message = Build(title, body, metadata);

        // FirebaseAdmin 3.6+ marks Message.Token obsolete in favour of Firebase installation ids (FIDs).
        // Kept deliberately: this adapter registers FCM registration tokens (the token-only decision
        // for iOS and Android alike), the FCM HTTP v1 API still accepts a token target, and the topic
        // APIs the broadcast depends on (SubscribeToTopicAsync / UnsubscribeFromTopicAsync) take only
        // registration tokens. Exit: when the topic APIs accept FIDs, move registration and send to
        // FIDs together and drop this suppression.
#pragma warning disable CS0618 // Type or member is obsolete
        message.Token = token;
#pragma warning restore CS0618 // Type or member is obsolete
        return message;
    }

    /// <summary>Builds the message for a topic (the broadcast).</summary>
    /// <param name="topic">The FCM topic.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata carried in both platform payloads.</param>
    /// <returns>The message.</returns>
    internal static Message ForTopic(string topic, string title, string body, IReadOnlyDictionary<string, string>? metadata)
    {
        var message = Build(title, body, metadata);
        message.Topic = topic;
        return message;
    }

    private static Message Build(string title, string body, IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        var hasMetadata = metadata is { Count: > 0 };
        return new Message
        {
            Notification = new Notification { Title = title, Body = body },
            Data = hasMetadata ? new Dictionary<string, string>(metadata!, StringComparer.Ordinal) : null,
            Apns = new ApnsConfig
            {
                Aps = new Aps { Alert = new ApsAlert { Title = title, Body = body } },
                CustomData = hasMetadata
                    ? metadata!
                        .Where(pair => !string.Equals(pair.Key, ApsKey, StringComparison.Ordinal))
                        .ToDictionary(pair => pair.Key, object (pair) => pair.Value, StringComparer.Ordinal)
                    : null,
            },
        };
    }
}
