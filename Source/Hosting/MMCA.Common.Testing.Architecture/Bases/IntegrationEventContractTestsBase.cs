namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Frozen wire-contract guard for the cross-service async API. A consumer in another service
/// deserializes integration events by shape, so a renamed/removed/retyped property — or a brand-new
/// event shipped without its consumer — silently breaks the contract. The subclass supplies the
/// committed <see cref="ExpectedContract"/>; this base rebuilds the live contract and compares. When a
/// change is intentional, version the event / coordinate the rollout and update ExpectedContract in the
/// same commit.
/// </summary>
public abstract class IntegrationEventContractTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>The committed snapshot: one line per integration event, "FullName { Prop:Type, ... }".</summary>
    protected abstract IReadOnlyList<string> ExpectedContract { get; }

    [Fact]
    public void IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot()
    {
        var actual = ArchitectureRules.BuildIntegrationEventContract(Map);

        actual.Should().Equal(
            ExpectedContract,
            "the integration-event wire contract changed — these events cross service boundaries over the "
            + "broker, so a renamed/removed/retyped property breaks consumers in other services. If "
            + "intentional, version the event / coordinate the consumer rollout, then update "
            + "ExpectedContract in this commit.");
    }
}
