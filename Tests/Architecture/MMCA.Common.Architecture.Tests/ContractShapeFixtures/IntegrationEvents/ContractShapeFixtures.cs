using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Architecture.Tests.ContractShapeFixtures.IntegrationEvents;

/// <summary>
/// An intermediate consumer base below the framework envelope. Its member is part of every derived
/// event's wire shape, so the frozen contract must pin it.
/// </summary>
internal abstract record FixtureShapedBase : BaseIntegrationEvent
{
    /// <summary>A member inherited by the derived event.</summary>
    public string Origin { get; init; } = string.Empty;
}

/// <summary>
/// An integration event whose members are constructed generics, so the frozen contract must spell
/// their generic arguments out rather than print the open-generic name.
/// </summary>
/// <param name="Count">A nullable value member.</param>
/// <param name="Tags">A generic collection member.</param>
internal sealed record FixtureShapedEvent(int? Count, IReadOnlyList<string> Tags) : FixtureShapedBase;

/// <summary>
/// An integration event with a multi-argument generic member, so the contract line carries a comma
/// inside angle brackets that the snapshot parser must not split on.
/// </summary>
/// <param name="Counts">A two-argument generic member.</param>
internal sealed record FixtureTallyEvent(IReadOnlyDictionary<string, int> Counts) : BaseIntegrationEvent;

/// <summary>Twin of <see cref="FixtureNullableCountEvent"/> differing only in the member's nullability.</summary>
/// <param name="Count">A non-nullable value member.</param>
internal sealed record FixtureCountEvent(int Count) : BaseIntegrationEvent;

/// <summary>Twin of <see cref="FixtureCountEvent"/> differing only in the member's nullability.</summary>
/// <param name="Count">A nullable value member.</param>
internal sealed record FixtureNullableCountEvent(int? Count) : BaseIntegrationEvent;

/// <summary>Twin of <see cref="FixtureNullableLabelEvent"/> differing only in nullable annotations.</summary>
/// <param name="Label">A non-nullable reference member.</param>
/// <param name="Notes">A generic member with a non-nullable reference argument.</param>
internal sealed record FixtureLabelEvent(string Label, IReadOnlyList<string> Notes) : BaseIntegrationEvent;

/// <summary>Twin of <see cref="FixtureLabelEvent"/> differing only in nullable annotations.</summary>
/// <param name="Label">A nullable reference member.</param>
/// <param name="Notes">A generic member with a nullable reference argument.</param>
internal sealed record FixtureNullableLabelEvent(string? Label, IReadOnlyList<string?> Notes) : BaseIntegrationEvent;

/// <summary>
/// An integration event declaring a stable wire identity, so the frozen contract must key it on
/// that name rather than on its CLR type name.
/// </summary>
/// <param name="Value">A plain member.</param>
[EventName("Fixture.Named.v1")]
internal sealed record FixtureNamedEvent(int Value) : BaseIntegrationEvent;
