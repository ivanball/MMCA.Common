using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Tests for the per-module escape on <c>ModulesDeclareLayers</c>. The rule's value is that it fails
/// when a repo forgets to register a module's assembly in its map, which is what stops every
/// per-module layer rule from passing vacuously. A repo with one deliberately thin module (no
/// aggregate, no persistence, so no Domain and no Infrastructure assembly exists to register) used to
/// have only one way to express that: trim the DEFAULT list, which stops enforcing those layers for
/// every OTHER module too. These tests pin that the override records the exception where it is true
/// and leaves the rest of the repo strict.
/// <para>
/// The map is a fixture rather than a real repo's, because the point under test is the rule's
/// arithmetic over module names and layers, not any particular assembly.
/// </para>
/// </summary>
public sealed class LayerDependencyOverrideTests
{
    private static readonly IReadOnlyList<Layer> FiveLayers =
        [Layer.Shared, Layer.Domain, Layer.Application, Layer.Infrastructure, Layer.Api];

    [Fact]
    public void ModulesDeclareLayers_FailsForAThinModule_WhenEveryModuleIsHeldToTheFullList()
    {
        var map = new FakeArchitectureMap(
            Full("Conference"),
            Thin("Notification"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(map, FiveLayers);

        act.Should().Throw<Exception>()
            .WithMessage("*module 'Notification' declares no Layer.Domain*");
    }

    [Fact]
    public void ModulesDeclareLayers_PassesForAThinModule_WhenTheOverrideNamesItsOwnLayers()
    {
        var map = new FakeArchitectureMap(
            Full("Conference"),
            Thin("Notification"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(
            map,
            FiveLayers,
            new Dictionary<string, IReadOnlyList<Layer>>(StringComparer.Ordinal)
            {
                ["Notification"] = [Layer.Shared, Layer.Application, Layer.Api],
            });

        act.Should().NotThrow();
    }

    [Fact]
    public void ModulesDeclareLayers_KeepsEveryOtherModuleStrict_WhileOneIsOverridden()
    {
        // This is the whole reason the override exists rather than a trimmed default list: one thin
        // module must not buy blanket permission to forget an assembly anywhere else.
        var map = new FakeArchitectureMap(
            Thin("Notification"),
            Thin("Conference"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(
            map,
            FiveLayers,
            new Dictionary<string, IReadOnlyList<Layer>>(StringComparer.Ordinal)
            {
                ["Notification"] = [Layer.Shared, Layer.Application, Layer.Api],
            });

        act.Should().Throw<Exception>()
            .WithMessage("*module 'Conference' declares no Layer.Domain*");
    }

    [Fact]
    public void ModulesDeclareLayers_OverrideCanAlsoDemandMore_NotJustLess()
    {
        // The override REPLACES the list for that module rather than subtracting from it, so a repo
        // can also hold one module to a stricter bar than the rest.
        var map = new FakeArchitectureMap(Full("Conference"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(
            map,
            FiveLayers,
            new Dictionary<string, IReadOnlyList<Layer>>(StringComparer.Ordinal)
            {
                ["Conference"] = [.. FiveLayers, Layer.Ui],
            });

        act.Should().Throw<Exception>()
            .WithMessage("*module 'Conference' declares no Layer.Ui*");
    }

    [Fact]
    public void ModulesDeclareLayers_TwoArgumentOverloadIsUnchanged()
    {
        // The existing public overload is what every current consumer calls; it must keep behaving as
        // the strict default.
        var map = new FakeArchitectureMap(Full("Conference"), Full("Engagement"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(map, FiveLayers);

        act.Should().NotThrow();
    }

    [Fact]
    public void ModulesDeclareLayers_NullOverrides_HoldEveryModuleToTheDefaultList()
    {
        var map = new FakeArchitectureMap(Thin("Notification"));

        Action act = () => ArchitectureRules.ModulesDeclareLayers(map, FiveLayers, overrides: null);

        act.Should().Throw<Exception>()
            .WithMessage("*module 'Notification' declares no Layer.Domain*");
    }

    private static IEnumerable<LayerRef> Full(string module) =>
        FiveLayers.Select(layer => Ref(module, layer));

    private static IEnumerable<LayerRef> Thin(string module) =>
        new[] { Layer.Shared, Layer.Application, Layer.Api }.Select(layer => Ref(module, layer));

    private static LayerRef Ref(string module, Layer layer) =>
        new(module, layer, typeof(LayerDependencyOverrideTests).Assembly, $"MMCA.Fake.{module}.{layer}");

    /// <summary>A map declaring exactly the module/layer pairs a test hands it.</summary>
    private sealed class FakeArchitectureMap(params IEnumerable<LayerRef>[] modules) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Fake";

        protected override IEnumerable<LayerRef> DefineLayers() => modules.SelectMany(m => m);
    }
}
