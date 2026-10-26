using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Architecture.Tests.UpcasterFixtures.IntegrationEvents;

/// <summary>First rung of the double-hop fixture (L242).</summary>
internal sealed record FixtureHopV1(string Sku) : BaseIntegrationEvent;

/// <summary>Second rung of the double-hop fixture.</summary>
internal sealed record FixtureHopV2(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 2;
}

/// <summary>Third rung of the double-hop fixture.</summary>
internal sealed record FixtureHopV3(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 3;
}

/// <summary>
/// Offender (L242): one class declaring two <c>IEventUpcaster&lt;,&gt;</c> contracts, V1 to V2 and
/// V2 to V3. The compiler requires the explicit non-generic <c>SourceType</c>, <c>TargetType</c> and
/// <c>Upcast</c> below (the two default interface implementations conflict), and they can report
/// only ONE source, so the registry would index the class as a single V1 to V2 hop and the V2 to V3
/// rung would silently vanish. That is the shape the unique-source rule must reject.
/// </summary>
internal sealed class FixtureDoubleHopUpcaster
    : IEventUpcaster<FixtureHopV1, FixtureHopV2>, IEventUpcaster<FixtureHopV2, FixtureHopV3>
{
    Type IEventUpcaster.SourceType => typeof(FixtureHopV1);

    Type IEventUpcaster.TargetType => typeof(FixtureHopV2);

    public FixtureHopV2 Upcast(FixtureHopV1 integrationEvent) => new(integrationEvent.Sku);

    public FixtureHopV3 Upcast(FixtureHopV2 integrationEvent) => new(integrationEvent.Sku);

    IIntegrationEvent IEventUpcaster.Upcast(IIntegrationEvent integrationEvent) =>
        Upcast((FixtureHopV1)integrationEvent);
}
