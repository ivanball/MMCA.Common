using System.Text.Json;

namespace MMCA.Common.Infrastructure.Notifications.Push;

/// <summary>
/// Builds the platform-native JSON payloads for OS-level push (ADR-044): the FCM v1 message and the
/// APNs alert. Azure Notification Hubs forwards them verbatim and SNS mobile push wraps them in its
/// per-platform message structure; the FCM adapter (MMCA.Common.Gcp) does not call them, because
/// FirebaseAdmin takes a typed message, so it maps the same fields and a test pins the equivalence.
/// Either way one notification looks the same on a device whichever cloud delivered it. Pure
/// System.Text.Json, no vendor SDK.
/// </summary>
public static class NativePushPayloads
{
    /// <summary>Builds the FCM v1 message payload (notification block + optional data keys).</summary>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata, carried as FCM data keys.</param>
    /// <returns>The <c>{"message":{...}}</c> FCM v1 payload.</returns>
    public static string BuildFcmV1Payload(string title, string body, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        var message = new Dictionary<string, object>
        {
            ["notification"] = new Dictionary<string, string> { ["title"] = title, ["body"] = body },
        };
        if (metadata is { Count: > 0 })
        {
            message["data"] = metadata;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = message });
    }

    /// <summary>Builds the APNs alert payload (aps block + metadata as top-level custom keys).</summary>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata, carried as top-level custom keys (never <c>aps</c>).</param>
    /// <returns>The APNs payload.</returns>
    public static string BuildApnsPayload(string title, string body, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        var payload = new Dictionary<string, object>
        {
            ["aps"] = new Dictionary<string, object>
            {
                ["alert"] = new Dictionary<string, string> { ["title"] = title, ["body"] = body },
            },
        };
        if (metadata is { Count: > 0 })
        {
            foreach (var (key, value) in metadata)
            {
                // "aps" is reserved by APNs; a metadata key must not clobber the alert block.
                if (!string.Equals(key, "aps", StringComparison.Ordinal))
                {
                    payload[key] = value;
                }
            }
        }

        return JsonSerializer.Serialize(payload);
    }
}
