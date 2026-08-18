using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Pins the ownership scoping of the integration-event rules: a module-bearing (consumer) map
/// must not see framework-shipped events (residency rules and frozen snapshots cover only the
/// repo's own contracts), while the framework's own module-less map keeps covering them at the
/// source. Regression guard for the Helpdesk canary break when the framework shipped its first
/// concrete integration event (<c>OutputCacheEvictionRequested</c>).
/// </summary>
public sealed class EventScopeFitnessTests
{
    [Fact]
    public void ModuleBearingMap_ExcludesFrameworkEvents_FromContractSnapshot()
    {
        var contract = ArchitectureRules.BuildIntegrationEventContract(new FakeConsumerMap());

        contract.Should().NotContain(
            line => line.Contains("OutputCacheEvictionRequested", StringComparison.Ordinal),
            because: "framework-owned events are the framework's contract, not the consumer's");
    }

    [Fact]
    public void ModuleBearingMap_PassesResidencyRule_DespiteFrameworkEvents()
    {
        // The framework Domain assembly hosts OutputCacheEvictionRequested outside any Shared
        // assembly; a consumer map must not flag it.
        var act = () => ArchitectureRules.IntegrationEventsResideInSharedIntegrationEventsNamespace(
            new FakeConsumerMap());

        act.Should().NotThrow();
    }

    [Fact]
    public void ModuleLessMap_StillCoversFrameworkEvents()
    {
        var contract = ArchitectureRules.BuildIntegrationEventContract(new CommonArchitectureMap());

        contract.Should().Contain(
            line => line.Contains("OutputCacheEvictionRequested", StringComparison.Ordinal),
            because: "the framework's own map is where its events stay covered");
    }

    /// <summary>
    /// A consumer-shaped map: the framework Domain assembly registered as a framework layer plus
    /// one module layer (this test assembly stands in; it ships no integration events).
    /// </summary>
    private sealed class FakeConsumerMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.FakeConsumer";

        protected override IEnumerable<LayerRef> DefineLayers()
        {
            yield return Framework(Layer.Domain, typeof(BaseIntegrationEvent).Assembly);
            yield return Module("Fake", Layer.Shared, typeof(EventScopeFitnessTests).Assembly);
        }
    }
}
