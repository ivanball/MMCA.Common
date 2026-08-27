using MMCA.Common.Architecture.Tests.DomainEventSaveFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Self-test for the domain-event-handler save rule shipped in
/// <c>MMCA.Common.Testing.Architecture</c> (<see cref="DomainEventHandlerSaveTestsBase"/>). A rule
/// that walks IL cannot be verified by reading it, so this points a map at THIS assembly, which
/// compiles the offending and the innocent handler shapes side by side in
/// <c>DomainEventSaveFixtures</c>, and pins the behaviours that matter: a direct save is caught, a
/// two-hop save through async services is caught, an interface call is resolved to its
/// implementation, a mutate-only handler is left alone, the allowlist prunes the walk, and the depth
/// bound is real.
/// </summary>
public sealed class DomainEventHandlerSaveFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.DomainEventSaveFixtures";

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void DirectSave_InTheHandler_IsFlagged()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(DirectSavingHandler),
                "a handler calling IUnitOfWork.SaveChangesAsync itself must be reported");
    }

    [Fact]
    public void TransitiveSave_TwoHopsAway_IsFlagged()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, []);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(TransitiveSavingHandler));
        message.Should().Contain(nameof(PointsAwarder), "the reported chain must name the first hop");
        message.Should().Contain(nameof(PointsWriter), "the reported chain must name the hop that saves");
        message.Should().Contain("SaveChangesAsync", "the reported chain must name the save it reaches");
    }

    [Fact]
    public void SaveBehindAnInterface_IsResolvedToItsImplementation()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(InterfaceDispatchSavingHandler),
                "IL records the interface call, so the walk must expand it to the implementations in scope");
    }

    [Fact]
    public void HandlerThatOnlyMutates_IsNotFlagged()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(InnocentHandler),
                "a handler that mutates in-memory state and returns is exactly what the rule protects");
    }

    [Fact]
    public void AllowlistedNamespace_SilencesTheRule()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, [FixtureNamespace]);

        act.Should().NotThrow(
            "a namespace entry covers the handlers under it, which is how a repo records a cascade it accepts while migrating");
    }

    [Fact]
    public void AllowlistedCollaborator_PrunesTheWalkWithoutHidingDirectSaves()
    {
        var allowed = $"{FixtureNamespace}.{nameof(PointsAwarder)}";

        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, [allowed]);

        var message = act.Should().Throw<XunitException>(
            "the handler that saves directly is still outside the allowlist").Which.Message;

        message.Should().NotContain(
            nameof(TransitiveSavingHandler),
            "an allowlisted collaborator stops the walk, so the handler behind it is no longer reached");
        message.Should().Contain(
            nameof(DirectSavingHandler),
            "a direct save is detected at the call site, so no allowlist entry on a collaborator can hide it");
    }

    [Fact]
    public void DepthBound_StopsTheWalk()
    {
        var act = () => ArchitectureRules.DomainEventHandlersDoNotSave(_map, [], maxCallDepth: 1);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(DirectSavingHandler), "a direct save needs no hops");
        message.Should().NotContain(
            nameof(TransitiveSavingHandler),
            "the two-hop save sits beyond a depth bound of one, which is the documented limit of the walk");
    }

    /// <summary>A map whose single layer is this test assembly, so the rule scans the fixtures above.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Application, typeof(FixtureDomainEvent).Assembly),
        ];
    }
}
