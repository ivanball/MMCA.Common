namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Records that an integration event (identified by its <see cref="MessageId"/>) has been
/// processed by this service, so a redelivery (at-least-once broker semantics) is skipped.
/// Mirrors the outbox and lives in the consumer service's own database.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>Gets the surrogate primary key.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the unique id of the processed integration event (the event's <c>MessageId</c>).</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Gets the event type name, retained for diagnostics.</summary>
    public required string EventType { get; init; }

    /// <summary>Gets the UTC timestamp when the event was processed.</summary>
    public DateTime ProcessedOn { get; init; }
}
