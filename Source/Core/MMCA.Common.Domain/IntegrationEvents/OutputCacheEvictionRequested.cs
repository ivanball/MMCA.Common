using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Domain.IntegrationEvents;

/// <summary>
/// Cross-service request to evict output-cache entries carrying the given tags. Published by the
/// service that owns the data (through the outbox, like any other integration event) and consumed
/// by every host that serves output-cached responses built from that data.
/// <para>
/// It exists because ASP.NET Core's output cache is per host: <c>IOutputCacheStore</c> is a local
/// store, so a write in the owning service leaves a stale cached response sitting in front of every
/// OTHER replica and every other service until its TTL expires. Broadcasting the eviction turns a
/// per-process concern into a fan-out one message wide.
/// </para>
/// <para>
/// <b>Frozen-contract candidate.</b> The wire shape is deliberately minimal: a tag list and nothing
/// else. Every host that consumes it must be able to deserialize it forever, so treat any change as
/// a versioning decision (ADR-010): additive optional fields keep <c>SchemaVersion</c> at 1, and a
/// rename, removal or retype requires a new event type plus a consumer-side upcaster, never a
/// silent reshape. That upcaster is registered with
/// <c>services.AddEventUpcaster&lt;OutputCacheEvictionRequested, OutputCacheEvictionRequestedV2, ...&gt;()</c>,
/// and every host still receiving the old contract over a broker adds
/// <c>x.RegisterUpcastedIntegrationEventConsumer&lt;OutputCacheEvictionRequested&gt;()</c> until the
/// queues drain (ADR-090).
/// </para>
/// </summary>
public sealed record class OutputCacheEvictionRequested : BaseIntegrationEvent
{
    /// <summary>
    /// The output-cache tags to evict, exactly as the producing host spelled them in its
    /// <c>[OutputCache(Tags = ...)]</c> / policy registration. Defaults to empty rather than being
    /// <c>required</c> so a message that arrives without the field deserializes into a harmless
    /// no-op instead of faulting the consumer and dead-lettering.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
