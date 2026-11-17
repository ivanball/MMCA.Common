using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Layering.SharedContracts;

/// <summary>
/// Pins <see cref="ArchitectureRules.ModuleSharedEnumValuesAreExplicit"/> against throwaway Shared
/// project folders written to a temp directory (explicit and implicit values are invisible in IL, so
/// the rule reads source), and the default folder lookup of <see cref="SharedLayerTestsBase"/>
/// against this repo's real <c>Source/</c> tree. The probe subclass is private so xUnit does not
/// collect its inherited facts as tests of their own.
/// </summary>
public sealed class ExplicitEnumValueRuleTests : IDisposable
{
    private const string ProjectFolder = "MMCA.Fake.Sales.Shared";

    private static readonly Assembly AnyAssembly = typeof(ExplicitEnumValueRuleTests).Assembly;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("shared-enums-");

    private string Project => Path.Combine(_root.FullName, ProjectFolder);

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void ExplicitValues_Pass_IncludingFlagsAttributesAndComments()
    {
        Write(
            "Orders/OrderStatus.cs",
            "namespace MMCA.Fake.Sales.Shared.Orders;",
            string.Empty,
            "/// <summary>An enum is persisted as its number.</summary>",
            "public enum OrderStatus : byte",
            "{",
            "    // Retired, (a commented-out member is not a member)",
            "    [Display(Name = \"Pending, awaiting payment\", Order = 1)]",
            "    Pending = 0,",
            "    Paid = 1, /* Shipped, */",
            "    [Description(@\"Cancelled, \"\"voided\"\" Refunded\")]",
            "    Cancelled = 2,",
            "    [Description(\"\"\"Archived, Hidden\"\"\")]",
            "    Archived = 3,",
            "}",
            string.Empty,
            "[Flags]",
            "public enum Access",
            "{",
            "    None = 0,",
            "    Read = 1 << 0,",
            "    Write = 1 << 1,",
            "    All = Read | Write,",
            "}");

        Check().Should().NotThrow("every member carries an explicit value; comments and attribute arguments are not members");
    }

    [Fact]
    public void ImplicitMember_IsReportedWithFileLineAndName()
    {
        Write(
            "Orders/Carrier.cs",
            "namespace MMCA.Fake.Sales.Shared.Orders;",
            string.Empty,
            "public enum Carrier",
            "{",
            "    Ups = 0,",
            "    Fedex,",
            "    Usps",
            "}");

        Check().Should().Throw<XunitException>()
            .Which.Message.Should().Contain($"{ProjectFolder}/Orders/Carrier.cs:6: Carrier.Fedex has no explicit value")
            .And.Contain($"{ProjectFolder}/Orders/Carrier.cs:7: Carrier.Usps has no explicit value",
                "a last member without a trailing comma is still a member");
    }

    [Fact]
    public void BuildOutputCopies_AreIgnored()
    {
        Write("obj/Debug/Stale.cs", "public enum Stale { A, B }");

        Check().Should().NotThrow("a file under obj is build output, not a declared contract");
    }

    [Fact]
    public void DefaultDirectories_ResolveModuleSharedLayersToTheirProjectFolders()
    {
        var probe = new Probe(
            new LayerRef("Fixture", Layer.Shared, AnyAssembly, "MMCA.Common.Shared"),
            new LayerRef(string.Empty, Layer.Shared, AnyAssembly, "MMCA.Common.Domain"),
            new LayerRef("Fixture", Layer.Application, AnyAssembly, "MMCA.Common.Application"));

        var sourceRoot = Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source");
        probe.Directories().Should().Equal(
            [Path.Combine(sourceRoot, "Core", "MMCA.Common.Shared")],
            "only a business module's Shared layer is scanned, located by its root namespace as a folder name");
    }

    [Fact]
    public void ModuleSharedWithNoFolder_FailsTheScanAsVacuous()
    {
        var probe = new Probe(new LayerRef("Fixture", Layer.Shared, AnyAssembly, "MMCA.Common.NoSuchShared"));

        var act = probe.ModuleShared_Enums_ShouldHave_ExplicitValues;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("no project folder of that name exists");
    }

    private Action Check() => () => ArchitectureRules.ModuleSharedEnumValuesAreExplicit([Project]);

    private void Write(string relativePath, params string[] lines)
    {
        var path = Path.Combine(Project, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines));
    }

    /// <summary>Exposes the protected default directory set for one declared map.</summary>
    private sealed class Probe(params LayerRef[] layers) : SharedLayerTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap(layers);

        public IReadOnlyList<string> Directories() => [.. ModuleSharedSourceDirectories()];
    }

    /// <summary>A map over exactly the layers a test declares, rooted at this repo.</summary>
    private sealed class FixtureMap(LayerRef[] layers) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() => layers;
    }
}
