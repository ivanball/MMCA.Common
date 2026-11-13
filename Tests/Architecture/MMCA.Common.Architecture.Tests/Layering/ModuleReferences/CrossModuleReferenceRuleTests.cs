using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering.ModuleReferences;

/// <summary>
/// Pins <see cref="ArchitectureRules.CrossModuleReferencesMatchAllowlist"/> and the
/// <see cref="CrossModuleReferenceTestsBase"/> wrapper against a throwaway module tree written to a temp
/// folder, since the framework itself has no modules for the rule to read. Each case builds the smallest
/// tree that decides it and asserts the failure names the offending edge or module.
/// </summary>
public sealed class CrossModuleReferenceRuleTests : IDisposable
{
    private const string FakeToken = "MMCA.Fake";
    private const string ModulesFolder = "Source/Modules";

    private static readonly string[] TwoModules = ["Alpha", "Beta"];

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("xmodref-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void DeclaredEdge_Passes()
    {
        AddProject("Alpha", "Application", "Beta.Shared");
        AddProject("Beta", "Shared");

        Check(["Alpha.Application -> Beta.Shared"]).Should().NotThrow();
    }

    [Fact]
    public void UndeclaredEdge_IsReportedWithItsProject()
    {
        AddProject("Alpha", "Application", "Beta.Shared");
        AddProject("Beta", "Shared");

        Check([]).Should().Throw<Exception>()
            .Which.Message.Should().Contain("Alpha.Application -> Beta.Shared", "the failure must name the coupling")
            .And.Contain("MMCA.Fake.Alpha.Application.csproj", "and the project file that declares it");
    }

    [Fact]
    public void StaleAllowlistEntry_IsReported()
    {
        AddProject("Alpha", "Application");
        AddProject("Beta", "Shared");

        Check(["Alpha.Application -> Beta.Shared"]).Should().Throw<Exception>()
            .Which.Message.Should().Contain("stale or misspelled", "a removed coupling must retire its allowlist entry");
    }

    [Fact]
    public void SameModuleReferences_AreNotCrossModuleEdges()
    {
        AddProject("Alpha", "Application", "Alpha.Domain");
        AddProject("Alpha", "Domain");
        AddProject("Beta", "Shared");

        Check([]).Should().NotThrow("a module's own layers are the layering rules' concern, not this one's");
    }

    [Fact]
    public void BuildOutputCopies_AreIgnored()
    {
        AddProject("Alpha", "Application");
        AddProject("Beta", "Shared");
        var copy = Path.Combine(_root.FullName, ModulesFolder, "Alpha", "MMCA.Fake.Alpha.Application", "obj", "MMCA.Fake.Alpha.Application.csproj");
        WriteProject(copy, ["Beta.Shared"]);

        Check([]).Should().NotThrow("a project file under obj is build output, not a declared reference");
    }

    [Fact]
    public void ModuleOnDiskButNotInTheMap_IsReported()
    {
        AddProject("Alpha", "Application");
        AddProject("Beta", "Shared");
        AddProject("Gamma", "Shared");

        Check([]).Should().Throw<Exception>()
            .Which.Message.Should().Contain("not declared by the architecture map: Gamma");
    }

    [Fact]
    public void ModuleInTheMapButNotOnDisk_IsReported()
    {
        AddProject("Alpha", "Application");

        Check([]).Should().Throw<Exception>(
            "a declared module with no project means the scan missed it, so the rule must not pass vacuously")
            .Which.Message.Should().Contain("no project found on disk: Beta");
    }

    [Fact]
    public void EmptyModuleSet_IsReported()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, ModulesFolder));

        var act = () => ArchitectureRules.CrossModuleReferencesMatchAllowlist(_root.FullName, ModulesFolder, FakeToken, [], []);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("declares no modules");
    }

    [Fact]
    public void MissingModulesFolder_IsReported() =>
        Check([]).Should().Throw<Exception>().Which.Message.Should().Contain("modules folder not found");

    [Fact]
    public void ProjectOutsideTheNamingConvention_IsReported()
    {
        AddProject("Alpha", "Application");
        AddProject("Beta", "Shared");
        WriteProject(Path.Combine(_root.FullName, ModulesFolder, "Beta", "Stray", "Stray.csproj"), []);

        Check([]).Should().Throw<Exception>()
            .Which.Message.Should().Contain("Stray.csproj", "a project the rule cannot place is one it cannot check");
    }

    [Fact]
    public void TestsBase_ReadsTheMapAndTheAllowlist()
    {
        AddProject("Alpha", "UI", "Beta.UI");
        AddProject("Beta", "UI");

        new Probe(_root.FullName, ["Alpha.UI -> Beta.UI"]).CrossModuleProjectReferences_ShouldMatch_TheAllowlist();

        var undeclared = new Probe(_root.FullName, []).CrossModuleProjectReferences_ShouldMatch_TheAllowlist;
        undeclared.Should().Throw<Exception>().Which.Message.Should().Contain("Alpha.UI -> Beta.UI");
    }

    private Action Check(IReadOnlyCollection<string> allowed) =>
        () => ArchitectureRules.CrossModuleReferencesMatchAllowlist(_root.FullName, ModulesFolder, FakeToken, TwoModules, allowed);

    /// <summary>Writes <c>Source/Modules/{module}/MMCA.Fake.{module}.{layer}/MMCA.Fake.{module}.{layer}.csproj</c>.</summary>
    private void AddProject(string module, string layer, params string[] references)
    {
        var name = $"{FakeToken}.{module}.{layer}";
        WriteProject(Path.Combine(_root.FullName, ModulesFolder, module, name, name + ".csproj"), references);
    }

    /// <summary>Writes an SDK project whose references point at sibling module projects, Windows separators included.</summary>
    private static void WriteProject(string path, IEnumerable<string> references)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var items = references.Select(reference =>
        {
            var module = reference[..reference.IndexOf('.', StringComparison.Ordinal)];
            var name = $"{FakeToken}.{reference}";
            return $"""    <ProjectReference Include="..\..\{module}\{name}\{name}.csproj" />""";
        });

        string[] lines = ["""<Project Sdk="Microsoft.NET.Sdk">""", "  <ItemGroup>", .. items, "  </ItemGroup>", "</Project>"];
        File.WriteAllLines(path, lines);
    }

    /// <summary>A consumer-shaped subclass, private so xUnit does not collect its inherited fact.</summary>
    private sealed class Probe(string repoRoot, IReadOnlyCollection<string> allowed) : CrossModuleReferenceTestsBase
    {
        protected override IArchitectureMap Map { get; } = new ProbeMap();

        protected override string RepoRoot => repoRoot;

        protected override IReadOnlyCollection<string> AllowedCrossModuleReferences => allowed;
    }

    /// <summary>A two-module map; the stand-in assembly is never scanned by this rule, only the module names are read.</summary>
    private sealed class ProbeMap : ArchitectureMapBase
    {
        public override string RepoToken => FakeToken;

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Alpha", Layer.Ui, typeof(ProbeMap).Assembly),
            Module("Beta", Layer.Ui, typeof(ProbeMap).Assembly),
        ];
    }
}
