using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Architecture.Tests.PayloadPurityFixtures.IntegrationEvents;

/// <summary>
/// A deliberately BAD integration event: its payload reaches a domain entity, one level down through
/// a nested record. Exists so the payload-purity rule can be proved to fire rather than merely
/// asserted to pass, and the nesting is the point: burying a domain type inside a payload record
/// hides the leak without removing it.
/// </summary>
internal sealed record FixtureLeakingEvent : BaseIntegrationEvent
{
    /// <summary>The nested payload that carries the leak.</summary>
    public FixtureLeakedPayload? Payload { get; init; }
}

/// <summary>The nested payload of <see cref="FixtureLeakingEvent"/>, typed on a domain entity.</summary>
/// <param name="Entity">The leaked domain type.</param>
internal sealed record FixtureLeakedPayload(AuditableBaseEntity<int>? Entity);

/// <summary>
/// A well-shaped integration event beside the bad one: primitives only, so the rule's failure
/// message must name the leaking event and leave this one out.
/// </summary>
/// <param name="Sku">A primitive payload field.</param>
internal sealed record FixtureCleanEvent(string Sku) : BaseIntegrationEvent;
