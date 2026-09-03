using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Represents a domain event persisted to the outbox table within the same transaction
/// as the aggregate changes. A background processor (<see cref="OutboxProcessor"/>)
/// retries unprocessed entries to guarantee at-least-once delivery.
/// </summary>
public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    /// <summary>
    /// Caches resolved event types per STORED name (an assembly-qualified name, or an
    /// <see cref="MMCA.Common.Domain.Attributes.EventNameAttribute"/> identity);
    /// <see cref="Type.GetType(string)"/> and the attribute scan behind it are per-call reflection
    /// lookups otherwise. Unresolvable names cache as null.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type?> EventTypeCache = new(StringComparer.Ordinal);

    /// <summary>Gets the unique identifier for this outbox entry.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the stored identity of the domain event, used to resolve its type on deserialization:
    /// the <see cref="MMCA.Common.Domain.Attributes.EventNameAttribute"/> name when the event
    /// declares one, otherwise its assembly-qualified type name.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>Gets the JSON-serialized domain event payload.</summary>
    public required string Payload { get; init; }

    /// <summary>Gets the UTC timestamp when the domain event was raised.</summary>
    public DateTime OccurredOn { get; init; }

    /// <summary>Gets or sets the UTC timestamp when the event was successfully dispatched. Null indicates pending.</summary>
    public DateTime? ProcessedOn { get; set; }

    /// <summary>Gets or sets the number of dispatch attempts by the outbox processor.</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp until which this row is leased to one processor replica.
    /// Rows with an unexpired lease are skipped by other replicas' polls, making scale-out safe
    /// by construction (before the lease, two replicas could drain the same rows and
    /// double-dispatch every event). Null or expired means unclaimed.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Gets or sets the claim token written together with <see cref="LockedUntil"/>; the claiming
    /// replica processes only rows carrying its own token, so a race between two claim updates
    /// cannot hand the same row to both.
    /// </summary>
    public Guid? LockToken { get; set; }

    /// <summary>Gets or sets the last error message from a failed dispatch attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets the W3C trace ID captured when the domain event was raised, for distributed tracing correlation.</summary>
    public string? TraceId { get; init; }

    /// <summary>Gets the W3C span ID captured when the domain event was raised, for distributed tracing correlation.</summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// Gets the ordered-delivery key copied from an event implementing
    /// <see cref="IHasOrderingKey"/>, or <see langword="null"/> for the unordered default.
    /// <para>
    /// A row carrying a key is not claimed while an earlier unprocessed, non-dead-lettered row with
    /// the same key exists in the same data source, so events for one aggregate reach the bus in the
    /// order they were raised even across batches and across processor replicas. See
    /// <see cref="IHasOrderingKey"/> for the head-of-line blocking this implies.
    /// </para>
    /// </summary>
    public string? OrderingKey { get; init; }

    /// <summary>
    /// Creates an <see cref="OutboxMessage"/> from a domain event, serializing it as JSON.
    /// </summary>
    /// <remarks>
    /// The stored <see cref="EventType"/> is the event's
    /// <see cref="MMCA.Common.Domain.Attributes.EventNameAttribute"/> name when it declares one, and
    /// its assembly-qualified name otherwise. Both lookups are cached per type by
    /// <see cref="EventNameResolver"/>, so annotating an event costs nothing per message.
    /// </remarks>
    /// <param name="domainEvent">The domain event to persist.</param>
    /// <returns>A new outbox message ready for persistence.</returns>
    public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var type = domainEvent.GetType();
        var activity = Activity.Current;
        return new OutboxMessage
        {
            EventType = EventNameResolver.GetStorageName(type),
            Payload = JsonSerializer.Serialize(domainEvent, type, SerializerOptions),
            OccurredOn = domainEvent.DateOccurred,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),

            // Opt-in: an event that does not implement the contract keeps a null key and the
            // unordered behavior. A null returned by an implementing event opts that one instance
            // out, which is why the interface check cannot be replaced by a type-level flag.
            OrderingKey = (domainEvent as IHasOrderingKey)?.OrderingKey,
        };
    }

    /// <summary>
    /// Deserializes the stored payload back into a domain event instance.
    /// </summary>
    /// <remarks>
    /// The type is resolved from the stored <see cref="EventType"/> alone. An event that declares
    /// <see cref="MMCA.Common.Domain.Attributes.EventNameAttribute"/> stores that name, so a rename,
    /// namespace move, or assembly move leaves every row still resolvable; an event without one
    /// stores its assembly-qualified name and is resolvable for as long as that name holds.
    /// </remarks>
    /// <returns>The deserialized domain event, or <see langword="null"/> if the type cannot be resolved.</returns>
    public IDomainEvent? DeserializeEvent()
    {
        var type = ResolveEventType();
        if (type is null)
            return null;

        // Deserialize with the same options used by FromDomainEvent so payloads written
        // with cycle-ignoring semantics read back symmetrically.
        return JsonSerializer.Deserialize(Payload, type, SerializerOptions) as IDomainEvent;
    }

    /// <summary>
    /// Resolves the stored <see cref="EventType"/> to a CLR type: as a CLR name first, then as an
    /// <see cref="MMCA.Common.Domain.Attributes.EventNameAttribute"/> identity. The result, including
    /// a failure, caches under the stored name.
    /// </summary>
    /// <returns>The resolved type, or <see langword="null"/> when the stored name matches nothing.</returns>
    private Type? ResolveEventType() =>
        // Order is load-bearing. Type.GetType stays first, so a row storing an assembly-qualified
        // name resolves by a direct lookup; the attribute scan only runs for a stored name that is
        // not a CLR name.
        EventTypeCache.GetOrAdd(
            EventType,
            static typeName => Type.GetType(typeName) ?? EventNameResolver.FindTypeByDeclaredName(typeName));
}
