namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Evolvability / drift fitness function (rubric §16, ADR-016 made executable): the MMCA.Common.*
/// packages are released in lockstep at one version and consumers are swept in a single pass, with no
/// phased rollout. Authored once here and re-run as a thin subclass in each consumer repo (each supplies
/// its <see cref="Map"/>). The rule reads the pinned versions in the repo's
/// <c>Directory.Packages.props</c> and fails the build if any <c>MMCA.Common.*</c> entry diverges, so a
/// partial sweep (bumping some packages but not all) is caught at CI time instead of producing a subtly
/// mismatched framework surface at runtime. MMCA.Common itself does not subclass this — it declares no
/// <c>MMCA.Common.*</c> package pins; only consumers do.
/// </summary>
public abstract class FrameworkVersionConsistencyTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Minimum number of <c>MMCA.Common.*</c> pins the scan must discover — a non-vacuous guard so a
    /// wrong path or a renamed prefix cannot let the gate pass with zero entries. Defaults to the
    /// released package count (see MMCA.Common/FACTS.md).
    /// </summary>
    protected virtual int MinimumCommonPackageCount => 13;

    [Fact]
    public void AllMmcaCommonPackages_ArePinnedToOneVersion()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var propsPath = Path.Combine(repoRoot, "Directory.Packages.props");

        var doc = XDocument.Load(propsPath);
        var commonPackages = doc
            .Descendants()
            .Where(static e => string.Equals(e.Name.LocalName, "PackageVersion", StringComparison.Ordinal))
            .Select(static e => new
            {
                Include = (string?)e.Attribute("Include"),
                Version = (string?)e.Attribute("Version"),
            })
            .Where(static p => p.Include is not null &&
                               p.Include.StartsWith("MMCA.Common.", StringComparison.Ordinal))
            .ToArray();

        (commonPackages.Length >= MinimumCommonPackageCount).Should().BeTrue(
            because: $"all {MinimumCommonPackageCount}+ MMCA.Common.* packages must be pinned in Directory.Packages.props, so the lockstep invariant is actually verified");

        commonPackages.Should().NotContain(
            p => string.IsNullOrEmpty(p.Version),
            because: "every MMCA.Common.* PackageVersion must declare an explicit version under central package management");

        var distinctVersions = commonPackages
            .Select(static p => p.Version)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        (distinctVersions.Length == 1).Should().BeTrue(
            because: $"all MMCA.Common.* packages must share one version (ADR-016 lockstep, no phased rollout); found: {string.Join(", ", distinctVersions)}");
    }
}
