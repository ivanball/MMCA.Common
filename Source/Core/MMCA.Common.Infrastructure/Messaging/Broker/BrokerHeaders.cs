namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// The transport-level header names every broker message carries, beside the origin headers in
/// <see cref="MMCA.Common.Shared.Messaging.MessageHeaders"/>. Every value is a string, so a broker that
/// widens or retypes application properties cannot change what the receiver reads.
/// </summary>
public static class BrokerHeaders
{
    /// <summary>The event's wire name; the receiver resolves the subscription and the CLR type from it.</summary>
    public const string EventName = "mmca-event-name";

    /// <summary>The integration event's own <c>MessageId</c>, the key the inbox dedupes on.</summary>
    public const string MessageId = "mmca-message-id";

    /// <summary>How many second-level redeliveries the message has had; absent means none.</summary>
    public const string RedeliveryCount = "mmca-redelivery-count";

    /// <summary>The body's media type.</summary>
    public const string ContentType = "content-type";

    /// <summary>The only body media type the framework writes.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>The W3C trace context parent, including the sampled flag.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>The W3C trace state.</summary>
    public const string TraceState = "tracestate";
}
