namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// A serialized integration event ready for a transport to publish. The framework builds it; the
/// transport writes <see cref="Body"/> and every entry of <see cref="Headers"/> unchanged (as application
/// properties, message attributes or columns) and publishes to <see cref="Topic"/>.
/// </summary>
/// <param name="Topic">The topic to publish on (<see cref="BrokerNaming.Topic"/>).</param>
/// <param name="EventName">The wire name, also present in <see cref="Headers"/> as <see cref="BrokerHeaders.EventName"/>.</param>
/// <param name="MessageId">The event's <c>MessageId</c>, also present as <see cref="BrokerHeaders.MessageId"/>; a transport with a native message id uses it.</param>
/// <param name="Body">The UTF-8 JSON body.</param>
/// <param name="Headers">Every header to carry, string to string.</param>
public sealed record OutgoingBrokerMessage(
    string Topic,
    string EventName,
    Guid MessageId,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers);
