namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Handler test-coverage fitness function (ADR-147): every command and query handler in the repo's
/// per-module Application assemblies is named, as a whole word, in at least one test source file, so
/// a new use case cannot ship without a test that at least constructs its handler. The check reads
/// source text under <see cref="TestsRoots"/> rather than test assemblies, which keeps it fast and
/// needs no extra references; the coverage ratchet catches what a name match cannot.
/// <para>
/// Adoption: subclass, supply <see cref="Map"/> and <see cref="RepoRoot"/>, run once, write the missing
/// tests, and freeze what genuinely needs none in <see cref="AllowedUntestedHandlers"/>, grouped by
/// reason the way <see cref="CommandValidatorCoverageTestsBase"/> adopters group theirs. An entry that
/// names no handler fails as stale, so the list can only shrink.
/// </para>
/// </summary>
public abstract class HandlerTestCoverageTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>Repo-relative folders (forward slashes) whose <c>*.cs</c> files count as tests.</summary>
    protected virtual IReadOnlyCollection<string> TestsRoots => ["Tests"];

    /// <summary>Handler type full names exempt from the rule. Empty by default.</summary>
    protected virtual IReadOnlyCollection<string> AllowedUntestedHandlers => [];

    /// <summary>
    /// Minimum number of handlers the scan must find, so a map that resolves to no module Application
    /// assemblies cannot pass without reading anything.
    /// </summary>
    protected virtual int MinimumHandlers => 1;

    [Fact]
    public void Handlers_ShouldBe_NamedByATest() =>
        ArchitectureRules.HandlersHaveTests(Map, RepoRoot, TestsRoots, AllowedUntestedHandlers);

    [Fact]
    public void HandlerInventory_ShouldNotBe_Empty() =>
        ArchitectureRules.HandlerCount(Map).Should().BeGreaterThanOrEqualTo(
            MinimumHandlers,
            because: "the rule must actually read a module's handlers; finding fewer means the map lost a module and the gate is vacuous");
}
