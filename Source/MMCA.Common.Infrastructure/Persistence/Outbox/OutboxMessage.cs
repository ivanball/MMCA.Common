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

    /// <summary>Gets or sets the last error message from a failed dispatch attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Creates an <see cref="OutboxMessage"/> from a domain event, serializing it as JSON.
    /// </summary>
    /// <param name="domainEvent">The domain event to persist.</param>
    /// <returns>A new outbox message ready for persistence.</returns>
    public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var type = domainEvent.GetType();
        return new OutboxMessage
        {
            EventType = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            Payload = JsonSerializer.Serialize(domainEvent, type, SerializerOptions),
            OccurredOn = domainEvent.DateOccurred,
        };
    }

    /// <summary>
    /// Deserializes the stored payload back into a domain event instance.
    /// </summary>
    /// <returns>The deserialized domain event, or <see langword="null"/> if the type cannot be resolved.</returns>
    public IDomainEvent? DeserializeEvent()
    {
        var type = Type.GetType(EventType);
        if (type is null)
            return null;

        return JsonSerializer.Deserialize(Payload, type) as IDomainEvent;
    }
}
