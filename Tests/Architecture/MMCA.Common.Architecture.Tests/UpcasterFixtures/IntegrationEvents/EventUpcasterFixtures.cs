using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Architecture.Tests.UpcasterFixtures.IntegrationEvents;

/// <summary>
/// Deliberately-shaped fixtures for <c>EventUpcasterFitnessTests</c>: one compliant version ladder,
/// one source contract claimed by two upcasters, and one upcaster pointing at a LOWER schema version.
/// <para>
/// The contracts live in a <c>*.IntegrationEvents</c> namespace so the residency rule (exercised over
/// this assembly by <c>EventScopeFitnessTests</c>' consumer-shaped map) stays satisfied, and they never
/// leave this test assembly, so no shipped event contract churns because of them.
/// </para>
/// </summary>
internal sealed record FixtureCompliantV1(string Sku) : BaseIntegrationEvent;

/// <summary>Compliant successor of <see cref="FixtureCompliantV1"/>.</summary>
internal sealed record FixtureCompliantV2(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 2;
}

/// <summary>Compliant terminal contract of the fixture ladder.</summary>
internal sealed record FixtureCompliantV3(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 3;
}

/// <summary>Source contract deliberately claimed by two upcasters.</summary>
internal sealed record FixtureContestedV1(string Sku) : BaseIntegrationEvent;

/// <summary>One of the two successors the contested source is upcast to.</summary>
internal sealed record FixtureContestedV2(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 2;
}

/// <summary>The other successor the contested source is upcast to.</summary>
internal sealed record FixtureContestedV3(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 3;
}

/// <summary>Older contract of the backwards pair: the upcaster points AT this one, which is the offence.</summary>
internal sealed record FixtureBackwardsV1(string Sku) : BaseIntegrationEvent;

/// <summary>Newer contract of the backwards pair, used as the upcaster's SOURCE.</summary>
internal sealed record FixtureBackwardsV2(string Sku) : BaseIntegrationEvent
{
    public override int SchemaVersion => 2;
}

/// <summary>Compliant: one claimant on V1, and the target declares a higher SchemaVersion.</summary>
internal sealed class FixtureCompliantV1ToV2Upcaster : IEventUpcaster<FixtureCompliantV1, FixtureCompliantV2>
{
    public FixtureCompliantV2 Upcast(FixtureCompliantV1 integrationEvent) => new(integrationEvent.Sku);
}

/// <summary>Compliant: the second rung of the same ladder.</summary>
internal sealed class FixtureCompliantV2ToV3Upcaster : IEventUpcaster<FixtureCompliantV2, FixtureCompliantV3>
{
    public FixtureCompliantV3 Upcast(FixtureCompliantV2 integrationEvent) => new(integrationEvent.Sku);
}

/// <summary>Offender: shares its source contract with <see cref="FixtureRivalClaimUpcaster"/>.</summary>
internal sealed class FixtureContestedClaimUpcaster : IEventUpcaster<FixtureContestedV1, FixtureContestedV2>
{
    public FixtureContestedV2 Upcast(FixtureContestedV1 integrationEvent) => new(integrationEvent.Sku);
}

/// <summary>Offender: the second claimant on <see cref="FixtureContestedV1"/>.</summary>
internal sealed class FixtureRivalClaimUpcaster : IEventUpcaster<FixtureContestedV1, FixtureContestedV3>
{
    public FixtureContestedV3 Upcast(FixtureContestedV1 integrationEvent) => new(integrationEvent.Sku);
}

/// <summary>Offender: upcasts a SchemaVersion 2 contract down to a SchemaVersion 1 one.</summary>
internal sealed class FixtureBackwardsVersionUpcaster : IEventUpcaster<FixtureBackwardsV2, FixtureBackwardsV1>
{
    public FixtureBackwardsV1 Upcast(FixtureBackwardsV2 integrationEvent) => new(integrationEvent.Sku);
}
