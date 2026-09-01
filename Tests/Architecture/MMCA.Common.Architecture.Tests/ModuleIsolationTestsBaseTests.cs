using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Coverage of the module-isolation rule set itself. MMCA.Common is module-less, so every rule here
/// is vacuous against the real map; a stub map is what makes the CROSS-LAYER pairs assertable.
/// <para>
/// The gap this pins: the six named rules cover only Domain/Application/Infrastructure/Api against
/// their OWN layer in another module, plus Domain and Application against another module's
/// Infrastructure. A module's Domain reaching another module's Application (or Api, or the reverse)
/// passed every gate, including the per-module layer rules, which forbid only the SAME module's
/// higher layers, and the compile-time guard, which only knows <c>MMCA.Common.*</c> references.
/// </para>
/// </summary>
public sealed class ModuleIsolationTestsBaseTests
{
    // Module "Beta" owns one Domain assembly. That assembly really does depend on
    // MMCA.Common.Application, and the map declares that namespace as another module's APPLICATION
    // layer, which is the Domain-to-other-Application pair nothing checked before.
    private static readonly StubMap CrossLayerViolation = new(
        fromLayer: Layer.Domain,
        forbiddenLayer: Layer.Application,
        forbiddenNamespace: "MMCA.Common.Application",
        assembly: typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly);

    [Fact]
    public void ModuleInternalLayersAreIsolated_CatchesADomainReachingAnotherModulesApplication()
    {
        var act = () => ArchitectureRules.ModuleInternalLayersAreIsolated(CrossLayerViolation);

        act.Should().Throw<Exception>("the full internal-layer cross product covers Domain to another module's Application");
    }

    [Fact]
    public void TheSixNamedRules_DoNotCoverThatPair()
    {
        // Documents exactly why the rule above exists: same violation, none of the shipped rules see
        // it. If one of them ever does, this test says so and the new rule can be re-scoped.
        var named = new Action[]
        {
            () => ArchitectureRules.ModuleDomainsAreIsolated(CrossLayerViolation),
            () => ArchitectureRules.ModuleApplicationsAreIsolated(CrossLayerViolation),
            () => ArchitectureRules.ModuleInfrastructuresAreIsolated(CrossLayerViolation),
            () => ArchitectureRules.ModuleApisAreIsolated(CrossLayerViolation),
            () => ArchitectureRules.ModuleDomainsDoNotReachOtherInfrastructures(CrossLayerViolation),
            () => ArchitectureRules.ModuleApplicationsDoNotReachOtherInfrastructures(CrossLayerViolation),
        };

        foreach (var rule in named)
        {
            rule.Should().NotThrow();
        }
    }

    [Fact]
    public void ModuleInternalLayersAreIsolated_IsSatisfiedWhenNothingCrossesAModuleBoundary()
    {
        var clean = new StubMap(
            fromLayer: Layer.Domain,
            forbiddenLayer: Layer.Application,
            forbiddenNamespace: "Some.Namespace.Nothing.References",
            assembly: typeof(Common.Domain.Entities.BaseEntity<>).Assembly);

        var act = () => ArchitectureRules.ModuleInternalLayersAreIsolated(clean);

        act.Should().NotThrow();
    }

    /// <summary>
    /// A hand-built map rather than an <see cref="ArchitectureMapBase"/> subclass: the base derives
    /// every other module's namespaces from the repo token, so it cannot express "one forbidden
    /// namespace, for one layer only", which is what isolates the pair under test.
    /// </summary>
    private sealed class StubMap(
        Layer fromLayer,
        Layer forbiddenLayer,
        string forbiddenNamespace,
        Assembly assembly) : IArchitectureMap
    {
        public string RepoToken => "MMCA.Fixture";

        public IReadOnlyList<string> ModuleNames => ["Beta", "Gamma"];

        public IReadOnlyList<LayerRef> Layers =>
            [new LayerRef("Beta", fromLayer, assembly, $"{RepoToken}.Beta.{fromLayer}")];

        public IEnumerable<Assembly> OfLayer(Layer layer) =>
            Layers.Where(l => l.Layer == layer).Select(l => l.Assembly);

        public IEnumerable<Assembly> ModuleDomain() => OfLayer(Layer.Domain);

        public IEnumerable<Assembly> ModuleApplication() => OfLayer(Layer.Application);

        public IEnumerable<Assembly> ModuleShared() => OfLayer(Layer.Shared);

        public IEnumerable<Assembly> Infrastructure() => OfLayer(Layer.Infrastructure);

        public IEnumerable<Assembly> Api() => OfLayer(Layer.Api);

        public Assembly? For(string module, Layer layer) =>
            Layers.FirstOrDefault(l => l.Layer == layer && string.Equals(l.Module, module, StringComparison.Ordinal))?.Assembly;

        public string ModuleOf(Assembly assembly) =>
            Layers.FirstOrDefault(l => l.Assembly == assembly)?.Module ?? string.Empty;

        public string RootNamespace(string module, Layer layer) => $"{RepoToken}.{module}.{layer}";

        public string[] OtherModuleNamespaces(string module, Layer layer) =>
            layer == forbiddenLayer ? [forbiddenNamespace] : [];
    }
}
