using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// The delivery body, in the Standard Webhooks payload shape:
/// <c>{"type":...,"timestamp":...,"data":...}</c>, with the event payload carried verbatim.
/// </summary>
public static class WebhookEnvelope
{
    /// <summary>Builds the body of one delivery.</summary>
    /// <param name="eventType">The webhook event type name.</param>
    /// <param name="occurredOn">When the event occurred; written as ISO-8601 UTC (<c>"O"</c>).</param>
    /// <param name="payloadJson">The event payload JSON, copied verbatim into <c>data</c>.</param>
    /// <returns>The JSON body.</returns>
    public static string Build(string eventType, DateTime occurredOn, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var utc = occurredOn.Kind switch
        {
            DateTimeKind.Utc => occurredOn,
            DateTimeKind.Local => occurredOn.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(occurredOn, DateTimeKind.Utc),
            _ => DateTime.SpecifyKind(occurredOn, DateTimeKind.Utc),
        };

        return new StringBuilder(payloadJson.Length + eventType.Length + 64)
            .Append("{\"type\":")
            .Append(JsonSerializer.Serialize(eventType))
            .Append(",\"timestamp\":\"")
            .Append(utc.ToString("O", CultureInfo.InvariantCulture))
            .Append("\",\"data\":")
            .Append(payloadJson)
            .Append('}')
            .ToString();
    }
}
