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
