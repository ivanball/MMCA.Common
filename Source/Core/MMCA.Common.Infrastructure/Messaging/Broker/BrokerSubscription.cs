namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// One integration event a host consumes: the wire name it travels under, the CLR type it deserializes
/// to, the topic it is published on, and how it is dispatched.
/// </summary>
/// <param name="EventName">The wire name (<see cref="BrokerNaming.EventName(Type)"/>), carried in the <c>mmca-event-name</c> header.</param>
/// <param name="EventType">The CLR type the body deserializes to.</param>
/// <param name="Topic">The topic the event is published on (<see cref="BrokerNaming.Topic"/>).</param>
/// <param name="Kind">Whether the event is dispatched as is or upcast first.</param>
public sealed record BrokerSubscription(string EventName, Type EventType, string Topic, BrokerConsumerKind Kind);
