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
    /// Region claims (however spelled — comparison is whitespace-insensitive and case-insensitive) that
    /// must NOT appear in <c>PRIVACY.md</c>, e.g. a stale pre-migration region or a foreign region copied
    /// from a sibling repo's policy.
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
        var normalizedPolicy = Normalize(privacyPolicy);

        normalizedPolicy.Should().Contain(Normalize(region),
            because: $"PRIVACY.md must state the actual data-storage region ('{region}') where the repo provisions the databases holding personal data (rubric §30)");

        foreach (var claim in ForbiddenResidencyClaims)
        {
            normalizedPolicy.Should().NotContain(Normalize(claim),
                because: $"the residency claim '{claim}' is stale or belongs to another deployment and must not appear in PRIVACY.md");
        }
    }

    /// <summary>
    /// Parses the region where the repo actually provisions its PII-bearing storage from the repo's own
    /// source of truth (a workflow default, an infra runbook, a Bicep parameter). Implementations should
    /// assert (with a clear <c>because</c>) when the expected marker is missing rather than return an
    /// empty string.
    /// </summary>
    protected abstract string ExtractDeployedRegion(string repoRoot);

    // Whitespace-stripped, upper-cased (CA1308 prefers ToUpperInvariant) so "West US 2" matches the
    // "westus2" region token and "Central US" matches "CentralUS".
    private static string Normalize(string text) =>
        string.Concat(text.Where(static c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
