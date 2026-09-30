namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Compliance drift fitness function (rubric §30): the data-residency statement published in a repo's
/// <c>PRIVACY.md</c> must match the region where personal data is actually provisioned. Authored once
/// here and re-run as a thin subclass in each repo: the subclass supplies its <see cref="Map"/> and
/// implements <see cref="ExtractDeployedRegion"/> against its own source of truth (e.g. ADC parses the
/// SQL region default out of <c>deploy.yml</c>; Store parses the single-region statement in
/// <c>infra/DISASTER-RECOVERY.md</c>). If either the deployed region or the privacy policy changes
/// without the other, the build fails — closing the gap where a policy once claimed a region the data
/// never lived in. <see cref="ForbiddenResidencyClaims"/> additionally blocks known-stale or copied
/// region claims from returning.
/// </summary>
public abstract class DataResidencyTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Region claims (however spelled: comparison is whitespace-insensitive, case-insensitive and
    /// whole-token: a region that is a prefix of another region does not match it) that must NOT appear
    /// in <c>PRIVACY.md</c>, e.g. a stale pre-migration region or a foreign region copied from a sibling
    /// repo's policy.
    /// </summary>
    protected virtual IReadOnlyList<string> ForbiddenResidencyClaims => [];

    [Fact]
    public void PrivacyPolicy_DataStorageRegion_MatchesDeployedRegion()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");

        var region = ExtractDeployedRegion(repoRoot);
        region.Should().NotBeNullOrWhiteSpace(
            because: "the deployed data-storage region must be parseable from the repo's infrastructure source of truth");

        var privacyPolicy = File.ReadAllText(Path.Combine(repoRoot, "PRIVACY.md"));

        ContainsRegionClaim(privacyPolicy, region).Should().BeTrue(
            because: $"PRIVACY.md must state the actual data-storage region ('{region}') where the repo provisions the databases holding personal data (rubric §30)");

        foreach (var claim in ForbiddenResidencyClaims)
        {
            ContainsRegionClaim(privacyPolicy, claim).Should().BeFalse(
                because: $"the residency claim '{claim}' is stale or belongs to another deployment and must not appear in PRIVACY.md");
        }
    }

    /// <summary>
    /// Whether <paramref name="policyText"/> states <paramref name="regionClaim"/> as a whole region
    /// token. Both sides are normalized first (whitespace stripped, upper-cased), then an occurrence
    /// counts only when the next character is not a digit and the text before it does not end with an
    /// Azure directional prefix, so <c>westus</c> does not match "West US 2" and <c>centralus</c> does
    /// not match "South Central US".
    /// </summary>
    /// <param name="policyText">The policy text to search.</param>
    /// <param name="regionClaim">The region token or spelled-out region name.</param>
    /// <returns><see langword="true"/> when a whole-token occurrence exists.</returns>
    protected static bool ContainsRegionClaim(string policyText, string regionClaim)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionClaim);

        var policy = Normalize(policyText);
        var claim = Normalize(regionClaim);
        for (var index = policy.IndexOf(claim, StringComparison.Ordinal);
             index >= 0;
             index = policy.IndexOf(claim, index + 1, StringComparison.Ordinal))
        {
            var end = index + claim.Length;
            var digitFollows = end < policy.Length && char.IsDigit(policy[end]);
            var prefixed = DirectionalPrefixes.Any(prefix =>
                policy.AsSpan(0, index).EndsWith(prefix, StringComparison.Ordinal));
            if (!digitFollows && !prefixed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses the region where the repo actually provisions its PII-bearing storage from the repo's own
    /// source of truth (a workflow default, an infra runbook, a Bicep parameter). Implementations should
    /// assert (with a clear <c>because</c>) when the expected marker is missing rather than return an
    /// empty string.
    /// </summary>
    protected abstract string ExtractDeployedRegion(string repoRoot);

    private static readonly string[] DirectionalPrefixes = ["NORTH", "SOUTH", "EAST", "WEST", "CENTRAL"];

    // Whitespace-stripped, upper-cased (CA1308 prefers ToUpperInvariant) so "West US 2" matches the
    // "westus2" region token and "Central US" matches "CentralUS".
    private static string Normalize(string text) =>
        string.Concat(text.Where(static c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
