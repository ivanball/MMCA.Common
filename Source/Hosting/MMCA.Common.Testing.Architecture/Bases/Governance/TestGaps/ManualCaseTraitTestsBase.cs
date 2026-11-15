namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Manual-case traceability fitness function (ADR-147): a test that verifies a manual test case says
/// so with <c>[Trait("Case", "&lt;id&gt;")]</c>, and this rule holds every such link to a case that
/// exists as a <c>### &lt;id&gt; </c> heading in the repo's manual suite (<see cref="SuiteFolder"/>).
/// It also floors the number of distinct linked cases, so links cannot be deleted silently. A missing
/// suite folder fails: there is nothing to resolve the links against, and that is never a pass.
/// <para>
/// Adoption: subclass, supply <see cref="RepoRoot"/>, and set <see cref="MinimumLinkedCases"/> to the
/// number of cases linked on the day the rule is wired; raise it as links are added.
/// </para>
/// </summary>
public abstract class ManualCaseTraitTestsBase
{
    /// <summary>The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The repo-relative folder whose <c>*.cs</c> files are scanned for <c>Case</c> traits.</summary>
    protected virtual string TestsRoot => "Tests";

    /// <summary>The repo-relative folder holding the manual suite's Markdown files.</summary>
    protected virtual string SuiteFolder => "Docs/ManualTestSuite";

    /// <summary>The minimum number of distinct cases the tests must link. Raise it as links are added.</summary>
    protected virtual int MinimumLinkedCases => 1;

    [Fact]
    public void CaseTraits_ShouldName_ExistingCases() =>
        ArchitectureRules.ManualCaseTraitsResolve(RepoRoot, TestsRoot, SuiteFolder);

    [Fact]
    public void LinkedCases_ShouldNotDrop() =>
        ArchitectureRules.LinkedManualCaseCount(RepoRoot, TestsRoot, SuiteFolder).Should().BeGreaterThanOrEqualTo(
            MinimumLinkedCases,
            because: "the count of manual cases linked from tests only grows; a drop means a Case trait was deleted");
}
