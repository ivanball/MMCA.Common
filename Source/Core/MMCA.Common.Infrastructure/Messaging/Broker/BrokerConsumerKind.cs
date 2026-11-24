namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>How the receive pipeline dispatches a subscription's messages.</summary>
public enum BrokerConsumerKind
{
    /// <summary>Delivered to the handlers of the event's own type.</summary>
    Current = 0,

    /// <summary>A retired contract: upcast to its terminal successor, then delivered to that type's handlers (ADR-090).</summary>
    Upcasted = 1,
}
