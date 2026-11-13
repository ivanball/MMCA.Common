namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Cross-module project-reference fitness function: the set of <c>ProjectReference</c> edges between
/// different modules is pinned in both directions against <see cref="AllowedCrossModuleReferences"/>, so a
/// new coupling between modules is a reviewed edit to the subclass rather than a silent line in a
/// <c>.csproj</c>, and a removed coupling retires its entry. It complements the IL-based
/// <see cref="ModuleIsolationTestsBase"/> and <see cref="SharedLayerTestsBase"/>, which cannot see a
/// reference that leaves nothing in the compiled assembly (an inlined <see langword="const"/>, a
/// <c>global using</c> alias). Authored once here and re-run as a thin subclass in each repo; a
/// single-module repo states an empty allowlist.
/// </summary>
public abstract class CrossModuleReferenceTestsBase
{
    /// <summary>The repo's architecture map, the source of its project prefix and declared modules.</summary>
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.
    /// </summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The repo-relative folder holding the module projects.</summary>
    protected virtual string ModulesFolder => "Source/Modules";

    /// <summary>
    /// The permitted cross-module edges, each written <c>"{Module}.{Layer} -&gt; {Module}.{Layer}"</c>
    /// (for example <c>"Conference.Application -&gt; Engagement.Shared"</c>).
    /// </summary>
    protected abstract IReadOnlyCollection<string> AllowedCrossModuleReferences { get; }

    [Fact]
    public void CrossModuleProjectReferences_ShouldMatch_TheAllowlist() =>
        ArchitectureRules.CrossModuleReferencesMatchAllowlist(
            RepoRoot, ModulesFolder, Map.RepoToken, Map.ModuleNames, AllowedCrossModuleReferences);
}
