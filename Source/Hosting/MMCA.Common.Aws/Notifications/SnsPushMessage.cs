using System.Text.Json;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// Builds the SNS mobile push message: a <c>MessageStructure=json</c> body with one entry per
/// protocol, so a single publish reaches an FCM endpoint, an APNs endpoint (production or sandbox)
/// and, through the broadcast topic, every subscribed endpoint at once. The platform payloads are the
/// core <see cref="NativePushPayloads"/>, the same bytes the Notification Hubs adapter sends.
/// </summary>
internal static class SnsPushMessage
{
    /// <summary>The <c>MessageStructure</c> value that makes SNS read the per-protocol entries.</summary>
    internal const string Structure = "json";

    /// <summary>
    /// Builds the message. The <c>"default"</c> entry carries the body text: SNS rejects a JSON-structured publish
    /// without it, and it is what a subscriber with no platform entry (an e-mail or SQS subscription
    /// added to the topic by an operator) receives. <c>GCM</c> wraps the FCM v1 message in the
    /// <c>fcmV1Message</c> envelope SNS expects for an FCM HTTP v1 platform application.
    /// </summary>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata carried in each platform payload.</param>
    /// <returns>The JSON message.</returns>
    internal static string Build(string title, string body, IReadOnlyDictionary<string, string>? metadata)
    {
        var fcm = NativePushPayloads.BuildFcmV1Payload(title, body, metadata);
        var apns = NativePushPayloads.BuildApnsPayload(title, body, metadata);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["default"] = body,
            ["GCM"] = $$"""{"fcmV1Message":{{fcm}}}""",
            ["APNS"] = apns,
            ["APNS_SANDBOX"] = apns,
        });
    }
}
