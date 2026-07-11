using System.Text.Json;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Builds the platform-native JSON payloads and user tag expressions for Azure Notification
/// Hubs sends (ADR-044). Kept as a pure helper so the payload shapes and the 20-tag chunking
/// rule are unit-testable without a hub.
/// </summary>
internal static class NativePushPayloads
{
    /// <summary>Azure Notification Hubs caps tag expressions at 20 tags; larger audiences are chunked.</summary>
    internal const int MaxTagsPerExpression = 20;

    /// <summary>Builds the FCM v1 message payload (notification block + optional data keys).</summary>
    internal static string BuildFcmV1Payload(string title, string body, IReadOnlyDictionary<string, string>? metadata = null)
    {
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
    internal static string BuildApnsPayload(string title, string body, IReadOnlyDictionary<string, string>? metadata = null)
    {
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

    /// <summary>
    /// Produces one <c>user:a || user:b || ...</c> OR-expression per chunk of
    /// <see cref="MaxTagsPerExpression"/> users.
    /// </summary>
    internal static IEnumerable<string> BuildUserTagExpressions(IEnumerable<UserIdentifierType> userIds) =>
        userIds
            .Select(UserTag)
            .Chunk(MaxTagsPerExpression)
            .Select(chunk => string.Join(" || ", chunk));

    /// <summary>The tag stamped on an installation to mark its owning user.</summary>
    internal static string UserTag(UserIdentifierType userId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"user:{userId}");
}
