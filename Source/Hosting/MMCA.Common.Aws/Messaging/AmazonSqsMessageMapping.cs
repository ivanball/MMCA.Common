using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using MMCA.Common.Infrastructure.Messaging.Broker;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// Maps between the framework's broker messages and SNS/SQS messages. The body travels as the UTF-8
/// JSON text. Every framework header travels packed into ONE string message attribute,
/// <see cref="HeadersAttribute"/>, holding a JSON object of string to string: a fully stamped message
/// carries up to 11 headers, and SNS and SQS both reject a message with more than 10 attributes, so one
/// attribute keeps the cap out of reach whatever the framework stamps. SNS raw message delivery copies
/// the attribute onto the SQS message unchanged, and the receiver unpacks it with the key case intact.
/// </summary>
internal static class AmazonSqsMessageMapping
{
    /// <summary>The message attribute that carries every header as one JSON object.</summary>
    internal const string HeadersAttribute = "mmca-headers";

    /// <summary>The header a dead-lettered message carries the pipeline's reason in.</summary>
    internal const string DeadLetterReasonHeader = "mmca-dead-letter-reason";

    /// <summary>The header a dead-lettered message carries the pipeline's detail in.</summary>
    internal const string DeadLetterDetailHeader = "mmca-dead-letter-detail";

    /// <summary>The longest dead-letter reason or detail written onto a message.</summary>
    internal const int MaxDeadLetterTextLength = 4096;

    private const string StringDataType = "String";

    /// <summary>Packs headers into the JSON object string <see cref="HeadersAttribute"/> carries.</summary>
    /// <param name="headers">The headers, string to string.</param>
    /// <returns>The JSON object text.</returns>
    internal static string PackHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in headers)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Unpacks the JSON object text of <see cref="HeadersAttribute"/>: every string-valued property,
    /// key case preserved. Text that is not a JSON object yields no headers (the receive pipeline then
    /// dead-letters the message as an unknown event, never guessing).
    /// </summary>
    /// <param name="packed">The attribute's text, or <see langword="null"/>.</param>
    /// <returns>The headers, compared ordinally.</returns>
    internal static Dictionary<string, string> UnpackHeaders(string? packed)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(packed))
        {
            return headers;
        }

        try
        {
            using var document = JsonDocument.Parse(packed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return headers;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    headers[property.Name] = property.Value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            headers.Clear();
        }

        return headers;
    }

    /// <summary>The SNS publish request for an outgoing broker message.</summary>
    /// <param name="topicArn">The event's topic.</param>
    /// <param name="message">The serialized broker message.</param>
    /// <returns>The request.</returns>
    internal static PublishRequest ToPublishRequest(string topicArn, OutgoingBrokerMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicArn);
        ArgumentNullException.ThrowIfNull(message);

        return new PublishRequest
        {
            TopicArn = topicArn,
            Message = Encoding.UTF8.GetString(message.Body.Span),
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>(StringComparer.Ordinal)
            {
                [HeadersAttribute] = new() { DataType = StringDataType, StringValue = PackHeaders(message.Headers) },
            },
        };
    }

    /// <summary>
    /// The headers of a received SQS message: any other string attribute under its own name (another
    /// publisher's), overlaid by the unpacked <see cref="HeadersAttribute"/>, so the framework's own
    /// headers always win.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <returns>The headers, compared ordinally.</returns>
    internal static Dictionary<string, string> ToHeaders(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? packed = null;
        foreach (var (name, value) in message.MessageAttributes ?? [])
        {
            if (value?.StringValue is not { } text)
            {
                continue;
            }

            if (string.Equals(name, HeadersAttribute, StringComparison.Ordinal))
            {
                packed = text;
            }
            else
            {
                headers[name] = text;
            }
        }

        foreach (var (key, value) in UnpackHeaders(packed))
        {
            headers[key] = value;
        }

        return headers;
    }

    /// <summary>The broker message the receive pipeline processes for a received SQS message.</summary>
    /// <param name="message">The received message.</param>
    /// <param name="headers">Its headers (<see cref="ToHeaders"/>).</param>
    /// <returns>The incoming broker message.</returns>
    internal static IncomingBrokerMessage ToIncoming(Message message, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(headers);

        return new IncomingBrokerMessage(
            Encoding.UTF8.GetBytes(message.Body ?? string.Empty),
            headers,
            message.MessageId ?? string.Empty);
    }

    /// <summary>
    /// The headers a second-level redelivery copy carries: every received header, with
    /// <see cref="BrokerHeaders.RedeliveryCount"/> set to <paramref name="redeliveryCount"/>.
    /// </summary>
    /// <param name="headers">The received headers.</param>
    /// <param name="redeliveryCount">The redelivery count the copy carries.</param>
    /// <returns>The copy's headers.</returns>
    internal static Dictionary<string, string> RedeliveryHeaders(IReadOnlyDictionary<string, string> headers, int redeliveryCount)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            [BrokerHeaders.RedeliveryCount] = redeliveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The headers a dead-lettered message carries: every received header plus
    /// <see cref="DeadLetterReasonHeader"/> and, when there is one, <see cref="DeadLetterDetailHeader"/>,
    /// each cut to <see cref="MaxDeadLetterTextLength"/>.
    /// </summary>
    /// <param name="headers">The received headers.</param>
    /// <param name="reason">The pipeline's reason.</param>
    /// <param name="detail">The pipeline's detail, or <see langword="null"/>.</param>
    /// <returns>The dead-lettered message's headers.</returns>
    internal static Dictionary<string, string> DeadLetterHeaders(IReadOnlyDictionary<string, string> headers, string reason, string? detail)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var result = new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            [DeadLetterReasonHeader] = Truncate(reason ?? string.Empty),
        };
        if (detail is not null)
        {
            result[DeadLetterDetailHeader] = Truncate(detail);
        }
        else
        {
            result.Remove(DeadLetterDetailHeader);
        }

        return result;
    }

    /// <summary>An SQS send request carrying <paramref name="body"/> and the packed <paramref name="headers"/>.</summary>
    /// <param name="queueUrl">The destination queue.</param>
    /// <param name="body">The body text.</param>
    /// <param name="headers">The headers to pack.</param>
    /// <param name="delaySeconds">The delay before the message becomes visible, 0 to 900 seconds.</param>
    /// <returns>The request.</returns>
    internal static SendMessageRequest ToSendRequest(string queueUrl, string body, IReadOnlyDictionary<string, string> headers, int delaySeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueUrl);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);

        return new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = body,
            DelaySeconds = delaySeconds,
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>(StringComparer.Ordinal)
            {
                [HeadersAttribute] = new() { DataType = StringDataType, StringValue = PackHeaders(headers) },
            },
        };
    }

    /// <summary>
    /// The SQS delay for a redelivery: the delay rounded up to whole seconds, clamped to 0 to
    /// <see cref="AmazonSqsTransport.MaxDelaySeconds"/> (the transport declares that cap as its
    /// <c>MaxRedeliveryDelay</c>, so the pipeline never asks for more).
    /// </summary>
    /// <param name="delay">The delay the pipeline asked for.</param>
    /// <returns>The delay in seconds.</returns>
    internal static int ToDelaySeconds(TimeSpan delay) =>
        (int)Math.Clamp(Math.Ceiling(delay.TotalSeconds), 0, AmazonSqsTransport.MaxDelaySeconds);

    private static string Truncate(string value) =>
        value.Length > MaxDeadLetterTextLength ? value[..MaxDeadLetterTextLength] : value;
}
