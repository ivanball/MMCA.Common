using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Layering.SharedContracts;

/// <summary>
/// Adversarial coverage for <c>ModuleSharedDoesNotDependOnOwnInternalLayers</c>: a module's Shared
/// layer is contracts only, so it must not reach its own Domain, Application, Infrastructure or API.
/// The map uses the repo token <c>MMCA</c> and the module <c>Common</c>, so the module's internal
/// namespaces are exactly the framework's real <c>MMCA.Common.{Domain,Application,...}</c>: the real
/// Shared assembly is then the compliant contracts layer and this test assembly, which references
/// every one of them, is the offending one.
/// </summary>
public sealed class ModuleSharedIsolationRuleTests
{
    /// <summary>The rule's message joins the two namespaces with a rightwards arrow (U+2192).</summary>
    private static readonly string Arrow = char.ConvertFromUtf32(0x2192);

    private static readonly Assembly RealShared = typeof(Common.Shared.Abstractions.Result).Assembly;

    private static readonly Assembly ReachesEveryLayer = typeof(ModuleSharedIsolationRuleTests).Assembly;

    [Fact]
    public void SharedReachingItsOwnDomain_IsFlagged()
    {
        var act = () => ArchitectureRules.ModuleSharedDoesNotDependOnOwnInternalLayers(
            new ModuleMap(new LayerRef("Common", Layer.Shared, ReachesEveryLayer, "MMCA.Common.Shared")));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"MMCA.Common.Shared {Arrow} MMCA.Common.Domain: Shared is a contracts-only layer",
                "the first internal layer the Shared assembly reaches is reported with both namespaces");
    }

    [Fact]
    public void ContractsOnlyShared_Passes()
    {
        var act = () => ArchitectureRules.ModuleSharedDoesNotDependOnOwnInternalLayers(
            new ModuleMap(new LayerRef("Common", Layer.Shared, RealShared, "MMCA.Common.Shared")));

        act.Should().NotThrow("the framework Shared assembly references no Domain, Application, Infrastructure or API namespace");
    }

    [Fact]
    public void FrameworkShared_AndOtherModuleLayers_AreNotRead()
    {
        var act = () => ArchitectureRules.ModuleSharedDoesNotDependOnOwnInternalLayers(new ModuleMap(
            new LayerRef(string.Empty, Layer.Shared, ReachesEveryLayer, "MMCA.Common.Shared"),
            new LayerRef("Common", Layer.Application, ReachesEveryLayer, "MMCA.Common.Application")));

        act.Should().NotThrow("only a business module's Shared layer is a contracts-only layer");
    }

    /// <summary>A map whose repo token makes module "Common" own the framework's real namespaces.</summary>
    private sealed class ModuleMap(params LayerRef[] layers) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA";

        protected override IEnumerable<LayerRef> DefineLayers() => layers;
    }
}
