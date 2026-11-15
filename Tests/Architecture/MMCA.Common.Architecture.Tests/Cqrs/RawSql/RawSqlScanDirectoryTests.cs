using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs.RawSql;

/// <summary>
/// Coverage for the default <c>RawSqlConventionTestsBase.ScannedSourceDirectories()</c>, which this
/// repo's own <c>RawSqlConventionTests</c> overrides and Store and ADC inherit. It locates each
/// business module's project folder under the repo's real <c>Source/</c> tree by the layer's root
/// namespace, so the map here names this repo's own project folders as module layers. The convention
/// subclass is private so xUnit does not collect its inherited fact as a test of its own.
/// </summary>
public sealed class RawSqlScanDirectoryTests
{
    private static readonly Assembly AnyAssembly = typeof(RawSqlScanDirectoryTests).Assembly;

    [Fact]
    public void ModuleLayers_ResolveToTheirProjectFolders_OncePerProject()
    {
        var probe = new ScanProbe(
            new LayerRef("Fixture", Layer.Infrastructure, AnyAssembly, "MMCA.Common.Infrastructure"),
            new LayerRef("Fixture", Layer.Application, AnyAssembly, "MMCA.Common.Infrastructure"),
            new LayerRef("Other", Layer.Application, AnyAssembly, "MMCA.Common.Application"));

        var directories = probe.Directories();

        var sourceRoot = Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source");
        directories.Should().Equal(
            [
                Path.Combine(sourceRoot, "Core", "MMCA.Common.Infrastructure"),
                Path.Combine(sourceRoot, "Core", "MMCA.Common.Application"),
            ],
            "each distinct module root namespace is looked up once, by folder name, anywhere under Source/");
    }

    [Fact]
    public void FrameworkLayers_AndUnknownProjects_ResolveToNothing()
    {
        var probe = new ScanProbe(
            new LayerRef(string.Empty, Layer.Infrastructure, AnyAssembly, "MMCA.Common.Infrastructure"),
            new LayerRef("Fixture", Layer.Api, AnyAssembly, "MMCA.Common.NoSuchProject"));

        probe.Directories().Should().BeEmpty(
            "a framework layer belongs to no module, and a module whose project folder does not exist contributes no directory");
    }

    [Fact]
    public void AMapWithNoModuleFolders_FailsTheScanAsVacuous()
    {
        var probe = new ScanProbe(new LayerRef(string.Empty, Layer.Application, AnyAssembly, "MMCA.Common.Application"));

        var act = probe.ModuleCode_UsesParameterizedSqlOnly;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("found no source directories to read");
    }

    /// <summary>Exposes the protected default directory set for one declared map.</summary>
    private sealed class ScanProbe(params LayerRef[] layers) : RawSqlConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap(layers);

        public IReadOnlyList<string> Directories() => [.. ScannedSourceDirectories()];
    }

    /// <summary>A map over exactly the layers a test declares, rooted at this repo.</summary>
    private sealed class FixtureMap(LayerRef[] layers) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() => layers;
    }
}
