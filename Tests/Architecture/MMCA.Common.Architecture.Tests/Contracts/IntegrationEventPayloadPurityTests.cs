using MMCA.Common.Architecture.Tests.PayloadPurityFixtures.IntegrationEvents;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Contracts;

/// <summary>
/// Runs the shared payload-purity rules (ADR-010) against MMCA.Common's own assemblies through
/// <see cref="IntegrationEventPayloadPurityTestsBase"/>, the same way MMCA.ADC and MMCA.Store
/// subclass it for theirs.
/// <para>
/// The residency half is vacuous here by design: MMCA.Common is the framework, not a module, so
/// there is no module boundary for a <c>*.Shared</c> assembly to be the public face of, and its one
/// shipped event lives beside the envelope it inherits. The payload half is NOT vacuous: it really
/// walks <c>OutputCacheEvictionRequested</c>, and the day the framework ships an event carrying a
/// domain type this build fails.
/// </para>
/// </summary>
public sealed class IntegrationEventPayloadPurityTests : IntegrationEventPayloadPurityTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}

/// <summary>
/// Adversarial coverage for the same two rules: a rule that only ever passes proves nothing. Both
/// are pointed at THIS test assembly, whose fixtures include a deliberately bad event, and both must
/// name it.
/// </summary>
public sealed class IntegrationEventPayloadPurityRuleTests
{
    [Fact]
    public void ResidencyRule_FlagsAnEventOutsideASharedAssembly()
    {
        var act = () => ArchitectureRules.IntegrationEventsLiveInSharedAssemblies(new ModuleProbeMap());

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(nameof(FixtureCleanEvent), "the test assembly is not a *.Shared assembly");
        message.Should().Contain("is not a *.Shared assembly");
    }

    [Fact]
    public void ResidencyRule_IsVacuousForAModuleLessMap() =>
        FluentActions.Invoking(() => ArchitectureRules.IntegrationEventsLiveInSharedAssemblies(new FrameworkProbeMap()))
            .Should().NotThrow(
                "a framework map has no module boundary, so its events answer to its public API "
                + "baseline rather than to a module's Shared assembly");

    [Fact]
    public void PayloadRule_FlagsADomainTypeReachedThroughANestedPayload()
    {
        var act = () => ArchitectureRules.IntegrationEventPayloadsAreDomainFree(new FrameworkProbeMap());

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(
            $"{nameof(FixtureLeakingEvent)}.{nameof(FixtureLeakingEvent.Payload)}.{nameof(FixtureLeakedPayload.Entity)}",
            "the message must name the PATH, because a leak one level down is the one a reviewer misses");
        message.Should().Contain("MMCA.Common.Domain", "and the assembly the leaked type came from");
    }

    [Fact]
    public void PayloadRule_LeavesACleanEventAlone()
    {
        var act = () => ArchitectureRules.IntegrationEventPayloadsAreDomainFree(new FrameworkProbeMap());

        act.Should().Throw<Exception>().Which.Message
            .Should().NotContain(
                nameof(FixtureCleanEvent),
                "an event whose payload is primitives only is exactly what the rule is asking for");
    }

    [Fact]
    public void PayloadRule_PassesOnTheFrameworksOwnAssemblies() =>
        FluentActions.Invoking(() => ArchitectureRules.IntegrationEventPayloadsAreDomainFree(new CommonArchitectureMap()))
            .Should().NotThrow("OutputCacheEvictionRequested carries a list of strings and nothing else");

    /// <summary>A module-less map over this test assembly: the payload rule scans it, the residency rule skips it.</summary>
    private sealed class FrameworkProbeMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Shared, typeof(FixtureLeakingEvent).Assembly)];
    }

    /// <summary>A module-bearing map over this test assembly, so the residency rule is in force.</summary>
    private sealed class ModuleProbeMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Module("Probe", Layer.Shared, typeof(FixtureLeakingEvent).Assembly)];
    }
}
