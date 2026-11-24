using System.Globalization;
using Azure.Messaging.ServiceBus;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// Maps between the framework's broker messages and Service Bus messages. Every framework header
/// travels as a string application property under its own key, so the receiver hands the pipeline
/// exactly the keys and values the publisher wrote.
/// </summary>
internal static class AzureServiceBusMessageMapping
{
    /// <summary>The Service Bus message for an outgoing broker message.</summary>
    /// <param name="message">The serialized broker message.</param>
    /// <returns>The message to send to the event's topic.</returns>
    internal static ServiceBusMessage ToServiceBusMessage(OutgoingBrokerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var serviceBusMessage = new ServiceBusMessage(message.Body)
        {
            MessageId = message.MessageId.ToString("D", CultureInfo.InvariantCulture),
            ContentType = BrokerHeaders.JsonContentType,
            Subject = message.EventName,
        };

        foreach (var (key, value) in message.Headers)
        {
            serviceBusMessage.ApplicationProperties[key] = value;
        }

        return serviceBusMessage;
    }

    /// <summary>
    /// The headers of a received message: every application property, key case preserved, values as
    /// strings (a non-string value written by another publisher is formatted with the invariant
    /// culture; a null value is dropped).
    /// </summary>
    /// <param name="properties">The received message's application properties.</param>
    /// <returns>The headers, compared ordinally.</returns>
    internal static Dictionary<string, string> ToHeaders(IReadOnlyDictionary<string, object> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var headers = new Dictionary<string, string>(properties.Count, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            // Convert.ToString turns null into an empty string, so null is skipped before it.
            if (value is not null)
            {
                headers[key] = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return headers;
    }

    /// <summary>The broker message the receive pipeline processes for a received Service Bus message.</summary>
    /// <param name="message">The received message.</param>
    /// <returns>The incoming broker message.</returns>
    internal static IncomingBrokerMessage ToIncoming(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new IncomingBrokerMessage(
            message.Body.ToMemory(),
            ToHeaders(message.ApplicationProperties),
            message.MessageId ?? string.Empty);
    }

    /// <summary>
    /// The copy a second-level redelivery schedules on the host's queue: the same body, message id,
    /// content type, subject and every application property, with
    /// <see cref="BrokerHeaders.RedeliveryCount"/> set to <paramref name="redeliveryCount"/>.
    /// </summary>
    /// <param name="message">The received message being redelivered.</param>
    /// <param name="redeliveryCount">The redelivery count the copy carries.</param>
    /// <returns>The copy to schedule.</returns>
    internal static ServiceBusMessage CreateRedelivery(ServiceBusReceivedMessage message, int redeliveryCount)
    {
        ArgumentNullException.ThrowIfNull(message);

        var copy = new ServiceBusMessage(message.Body)
        {
            MessageId = message.MessageId,
            ContentType = message.ContentType,
            Subject = message.Subject,
        };

        foreach (var (key, value) in message.ApplicationProperties)
        {
            copy.ApplicationProperties[key] = value;
        }

        copy.ApplicationProperties[BrokerHeaders.RedeliveryCount] = redeliveryCount.ToString(CultureInfo.InvariantCulture);

        return copy;
    }
}
