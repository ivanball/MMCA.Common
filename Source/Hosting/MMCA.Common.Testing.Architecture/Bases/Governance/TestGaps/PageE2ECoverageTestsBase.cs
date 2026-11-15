namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Page E2E-coverage fitness function (ADR-147): every <c>@page</c> route under <see cref="SourceRoot"/>
/// is reached by at least one string literal in the E2E test sources, so a new page cannot ship
/// without an end-to-end test that opens it. A route template's <c>{param}</c> segments match any one
/// concrete segment or an interpolation hole; a query string and a leading <c>{BaseUrl}</c>-style hole
/// in the literal are ignored.
/// <para>
/// Adoption: subclass, supply <see cref="RepoRoot"/>, and list in <see cref="AllowedUntestedRoutes"/>
/// (by template, grouped by reason) the routes an E2E run genuinely cannot reach, such as a device-only
/// page or one behind an external provider's challenge.
/// </para>
/// </summary>
public abstract class PageE2ECoverageTestsBase
{
    /// <summary>The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The repo-relative folder holding the <c>.razor</c> pages.</summary>
    protected virtual string SourceRoot => "Source";

    /// <summary>Repo-relative folders (forward slashes) whose <c>*.cs</c> files are E2E tests or page objects.</summary>
    protected virtual IReadOnlyCollection<string> E2ESourceRoots => ["Tests/E2E"];

    /// <summary>Route templates exempt from the rule, exactly as their <c>@page</c> directive spells them.</summary>
    protected virtual IReadOnlyCollection<string> AllowedUntestedRoutes => [];

    /// <summary>Minimum number of routes the scan must find, so a wrong <see cref="SourceRoot"/> cannot pass vacuously.</summary>
    protected virtual int MinimumRoutes => 1;

    [Fact]
    public void Pages_ShouldBe_ReachedByAnE2ETest() =>
        ArchitectureRules.PagesHaveE2ETests(RepoRoot, SourceRoot, E2ESourceRoots, AllowedUntestedRoutes);

    [Fact]
    public void RouteInventory_ShouldNotBe_Empty() =>
        ArchitectureRules.PageRouteCount(RepoRoot, SourceRoot).Should().BeGreaterThanOrEqualTo(
            MinimumRoutes,
            because: "the rule must actually read the repo's pages; finding fewer means the source root is wrong and the gate is vacuous");
}
