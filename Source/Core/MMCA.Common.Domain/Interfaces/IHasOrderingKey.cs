namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Opt-in contract for a domain or integration event that must be delivered in order relative to
/// other events sharing the same <see cref="OrderingKey"/>. Implement it on the event record (most
/// often a <c>BaseIntegrationEvent</c>) and return a value that identifies the entity whose event
/// stream must stay sequential, typically the aggregate id: <c>"order-1042"</c>,
/// <c>$"cart-{CartId}"</c>.
/// <para>
/// The outbox copies the value onto the row it writes, and the processor refuses to claim a row
/// while an EARLIER unprocessed, non-dead-lettered row carrying the same key exists in the same
/// data source. Ordering therefore holds across batches and across scaled-out processor replicas,
/// not merely within one batch. One cycle delivers one row per key; when it delivers anything, the
/// key-mates it deferred are picked up on an immediate re-poll rather than after the polling
/// interval.
/// </para>
/// <para>
/// This is head-of-line blocking by design: a keyed row that is failing and backing off blocks
/// every later row with the same key until it succeeds or exhausts its retries (a dead-lettered
/// row stops blocking, so one poison event cannot freeze its key forever). Keys must therefore be
/// as NARROW as the ordering requirement really is: one key per aggregate serializes that
/// aggregate only, while a constant key serializes the whole outbox. An event that does not
/// implement this interface keeps the unordered, fully parallel behavior.
/// </para>
/// <para>
/// The poll fetches the oldest <c>BatchSize</c> pending rows with no key awareness, so a key with
/// <c>BatchSize</c> or more pending rows fills the fetch window: a bursty or blocked key delays
/// unrelated newer rows until the burst drains or the head row dead-letters.
/// </para>
/// </summary>
public interface IHasOrderingKey
{
    /// <summary>
    /// Gets the ordering key, or <see langword="null"/> to opt this individual event out of ordered
    /// delivery even though its type implements the interface.
    /// </summary>
    string? OrderingKey { get; }
}
