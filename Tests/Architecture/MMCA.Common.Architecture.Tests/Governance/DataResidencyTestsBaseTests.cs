using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Pins the whole-token region comparison of <see cref="DataResidencyTestsBase"/>. The inherited fact
/// reads the real repo root, so the comparison helper is the testable unit; the probe subclass is
/// private so xUnit does not collect its inherited fact as a test of its own.
/// </summary>
public sealed class DataResidencyTestsBaseTests
{
    [Theory]
    [InlineData("stored in the West US 2 region", "westus", false)]
    [InlineData("stored in the West US 2 region", "westus2", true)]
    [InlineData("in the South Central US region", "centralus", false)]
    [InlineData("in the Central US region", "centralus", true)]
    [InlineData("hosted in the **West US 2** Azure region", "westus2", true)]
    [InlineData("the **Central US** region", "Central US", true)]
    public void ContainsRegionClaim_MatchesWholeRegionTokensOnly(string policy, string claim, bool expected) =>
        Probe.Contains(policy, claim).Should().Be(
            expected,
            "a region that is a prefix of another region must not satisfy the residency gate");

    private sealed class Probe : DataResidencyTestsBase
    {
        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

        public static bool Contains(string policy, string claim) => ContainsRegionClaim(policy, claim);

        protected override string ExtractDeployedRegion(string repoRoot) => string.Empty;
    }
}
