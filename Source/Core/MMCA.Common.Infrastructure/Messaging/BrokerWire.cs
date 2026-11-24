using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// The pieces of the broker wire format the publish side (<see cref="BrokerMessageBus"/>) and the
/// receive side (the receive pipeline) must agree on: the JSON options the body is written and read
/// with, and the trace source both hops report on.
/// </summary>
internal static class BrokerWire
{
    /// <summary>
    /// The trace source of the broker hop: one Producer span per publish and one Consumer span per
    /// delivery, parented across the broker through the W3C <c>traceparent</c> header. Subscribed by
    /// name in MMCA.Common.Aspire, which has no reference to this package.
    /// </summary>
    internal const string ActivitySourceName = "MMCA.Common.Messaging";

    /// <summary>The <c>messaging.system</c> value stamped on both spans.</summary>
    internal const string MessagingSystem = "mmca";

    /// <summary>The shared trace source (see <see cref="ActivitySourceName"/>).</summary>
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// The body serializer options. Identical to the options <c>OutboxMessage</c> serializes its
    /// payload with (cycle-ignoring, otherwise the System.Text.Json defaults), so an event that
    /// round-trips through the outbox round-trips through the broker in exactly the same shape. Kept
    /// as a copy rather than a shared reference because the outbox's instance is private to the
    /// persistence layer.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };
}
