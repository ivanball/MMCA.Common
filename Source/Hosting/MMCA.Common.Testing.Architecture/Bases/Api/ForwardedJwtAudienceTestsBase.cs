namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fail-closed audience gate (ADR-004): every service host that validates forwarded tokens through
/// <c>AddForwardedJwtBearer</c> passes its audience as
/// <c>JwtAudience.RequireConfigured(configuration[JwtAudience.ConfigKey])</c>, with no <c>??</c>
/// fallback, so a host deployed without <c>Jwt:Audience</c> fails at startup instead of validating
/// against a hard-coded default. Authored once here and re-run as a thin subclass in each repo: the
/// subclass supplies its <see cref="Map"/> and the number of hosts it expects to find.
/// </summary>
public abstract class ForwardedJwtAudienceTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The fewest <c>AddForwardedJwtBearer</c> calls the scan must find, normally the repo's number of
    /// extracted service hosts that are not the token issuer. A floor keeps the gate non-vacuous.
    /// </summary>
    protected abstract int MinimumForwardedJwtHosts { get; }

    /// <summary>
    /// The directories scanned for <c>*.cs</c> files. Defaults to the repo's <c>Source</c> folder.
    /// </summary>
    protected virtual IReadOnlyCollection<string> SourceRoots =>
        [Path.Combine(ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx"), "Source")];

    [Fact]
    public void ForwardedJwtBearerHosts_ResolveTheirAudienceFailClosed() =>
        ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed(SourceRoots, MinimumForwardedJwtHosts);
}
