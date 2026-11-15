using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.TestGaps;

/// <summary>
/// Self-test for the manual-case traceability rule (<see cref="ManualCaseTraitTestsBase"/>), over
/// throwaway trees. It pins: a <c>[Trait("Case", id)]</c> naming an id the suite folder does not hold
/// is reported with the id and the test file, the linked-case count reads distinct ids, a linked count
/// under the minimum fails, and a missing suite folder fails closed instead of passing.
/// </summary>
public sealed class ManualCaseTraitFitnessTests
{
    private const string Suite = "# Suite\n\n## Area\n\n### U-01 Home page renders\n\nPriority: P1 | Platform: Web | Auto: yes\n\n### U-02 Login works\n";

    [Fact]
    public void TraitNamingAnUnknownCase_IsFlaggedWithIdAndFile()
    {
        using var repo = new TempRepo()
            .Write("Docs/ManualTestSuite/01-unauth.md", Suite)
            .Write("Tests/E2E/HomeTests.cs", "[Trait(\"Case\", \"U-01\")]\n[Trait(\"Case\", \"U-99\")]\npublic void Home() { }");

        var act = () => ArchitectureRules.ManualCaseTraitsResolve(repo.Root, "Tests", "Docs/ManualTestSuite");

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("U-99").And.Contain("HomeTests.cs").And.NotContain("U-01:");
    }

    [Fact]
    public void TraitsNamingKnownCases_Pass()
    {
        using var repo = new TempRepo()
            .Write("Docs/ManualTestSuite/01-unauth.md", Suite)
            .Write("Tests/E2E/HomeTests.cs", "[Trait(\"Case\", \"U-01\")]\npublic void Home() { }")
            .Write("Tests/Unit/LoginTests.cs", "[Trait( \"Case\" , \"U-02\" )]\npublic void Login() { }");

        var act = () => ArchitectureRules.ManualCaseTraitsResolve(repo.Root, "Tests", "Docs/ManualTestSuite");

        act.Should().NotThrow("both traits name cases the suite holds");
    }

    [Fact]
    public void LinkedCaseCount_CountsDistinctIds()
    {
        using var repo = new TempRepo()
            .Write("Docs/ManualTestSuite/01-unauth.md", Suite)
            .Write("Tests/E2E/HomeTests.cs", "[Trait(\"Case\", \"U-01\")]\n[Trait(\"Case\", \"U-01\")]")
            .Write("Tests/Unit/LoginTests.cs", "[Trait(\"Case\", \"U-02\")]");

        ArchitectureRules.LinkedManualCaseCount(repo.Root, "Tests", "Docs/ManualTestSuite")
            .Should().Be(2, "U-01 linked twice is still one linked case");
    }

    [Fact]
    public void LinkedCountUnderTheMinimum_Fails()
    {
        using var repo = new TempRepo()
            .Write("Docs/ManualTestSuite/01-unauth.md", Suite)
            .Write("Tests/E2E/HomeTests.cs", "[Trait(\"Case\", \"U-01\")]");
        var sut = new LinkedFloor(repo.Root, minimum: 2);

        var act = sut.LinkedCases_ShouldNotDrop;

        act.Should().Throw<XunitException>("one linked case against a floor of two means a link was deleted");
    }

    [Fact]
    public void MissingSuiteFolder_FailsClosed()
    {
        using var repo = new TempRepo().Write("Tests/E2E/HomeTests.cs", "[Trait(\"Case\", \"U-01\")]");

        var act = () => ArchitectureRules.ManualCaseTraitsResolve(repo.Root, "Tests", "Docs/ManualTestSuite");

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("Docs/ManualTestSuite", "no suite to resolve against is a failure, never a pass");
    }

    private sealed class LinkedFloor(string root, int minimum) : ManualCaseTraitTestsBase
    {
        protected override string RepoRoot => root;

        protected override int MinimumLinkedCases => minimum;
    }
}
