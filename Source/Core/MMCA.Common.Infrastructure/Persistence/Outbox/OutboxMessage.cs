using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Domain.Interfaces;

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
    /// Caches resolved event types per assembly-qualified name; <see cref="Type.GetType(string)"/>
    /// is a per-call reflection lookup otherwise. Unresolvable names cache as null.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type?> EventTypeCache = new(StringComparer.Ordinal);

    /// <summary>Gets the unique identifier for this outbox entry.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the assembly-qualified type name of the domain event for deserialization.</summary>
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
    /// <param name="domainEvent">The domain event to persist.</param>
    /// <returns>A new outbox message ready for persistence.</returns>
    public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var type = domainEvent.GetType();
        var activity = Activity.Current;
        return new OutboxMessage
        {
            EventType = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
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
    /// <returns>The deserialized domain event, or <see langword="null"/> if the type cannot be resolved.</returns>
    public IDomainEvent? DeserializeEvent() => DeserializeEvent(typeAliases: null);

    /// <summary>
    /// Deserializes the stored payload back into a domain event instance, consulting
    /// <paramref name="typeAliases"/> when the stored type name no longer resolves (the event class
    /// was renamed, moved to another namespace, or moved to another assembly after the row was
    /// written).
    /// </summary>
    /// <param name="typeAliases">
    /// Map of stored name to replacement type, keyed either by the full stored assembly-qualified
    /// name or by its type-full-name portion (the text before the first comma). The value may be an
    /// assembly-qualified name or a bare full name, which is then searched across the loaded
    /// assemblies. Pass <see langword="null"/> for no aliasing.
    /// </param>
    /// <returns>The deserialized domain event, or <see langword="null"/> if the type cannot be resolved.</returns>
    public IDomainEvent? DeserializeEvent(IReadOnlyDictionary<string, string>? typeAliases)
    {
        var type = ResolveEventType(typeAliases);
        if (type is null)
            return null;

        // Deserialize with the same options used by FromDomainEvent so payloads written
        // with cycle-ignoring semantics read back symmetrically.
        return JsonSerializer.Deserialize(Payload, type, SerializerOptions) as IDomainEvent;
    }

    /// <summary>
    /// Resolves the stored <see cref="EventType"/> to a CLR type: the stored name first, then the
    /// alias map. Alias lookups are only paid for by rows whose stored name failed to resolve, and
    /// the successful lookups cache under the ALIAS TARGET rather than the stored name, so two hosts
    /// configured with different maps cannot poison each other's cache entries.
    /// </summary>
    private Type? ResolveEventType(IReadOnlyDictionary<string, string>? typeAliases)
    {
        var type = EventTypeCache.GetOrAdd(EventType, static typeName => Type.GetType(typeName));
        if (type is not null || typeAliases is null || typeAliases.Count == 0)
            return type;

        if (!typeAliases.TryGetValue(EventType, out var target))
        {
            // The stored value is an assembly-qualified name, while an operator writing a
            // configuration key naturally reaches for the type name alone. Accept both.
            var commaIndex = EventType.IndexOf(',', StringComparison.Ordinal);
            var fullName = commaIndex < 0 ? EventType : EventType[..commaIndex].Trim();
            if (!typeAliases.TryGetValue(fullName, out target))
                return null;
        }

        return string.IsNullOrWhiteSpace(target)
            ? null
            : EventTypeCache.GetOrAdd(target, static name => Type.GetType(name) ?? FindLoadedType(name));
    }

    /// <summary>
    /// Last resort for an alias target written as a bare full name (no assembly): scans the loaded
    /// assemblies for it. Only ever reached once per alias target, because the result caches.
    /// </summary>
    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            var candidate = assembly.GetType(fullName, throwOnError: false);
            if (candidate is not null)
                return candidate;
        }

        return null;
    }
}
