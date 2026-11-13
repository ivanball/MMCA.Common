namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fail-closed audience gate (ADR-004): every service host that validates forwarded tokens resolves
/// its audience as <c>JwtAudience.RequireConfigured(configuration[JwtAudience.ConfigKey])</c>, with no
/// <c>??</c> fallback, so a host deployed without <c>Jwt:Audience</c> fails at startup instead of
/// validating against a hard-coded default. A host does so in one of two shapes: it calls
/// <c>AddForwardedJwtBearer</c> directly, and the scan checks that call's audience argument; or it calls
/// the composed <c>AddMmcaServiceHost</c> (MMCA.Common.ServiceHost) without selecting
/// <c>ServiceHostAuthentication.TokenIssuer</c>, and the composed host makes the guarded call itself, so
/// the scan only counts it. Authored once here and re-run as a thin subclass in each repo: the subclass
/// supplies its <see cref="Map"/> and the number of hosts it expects to find.
/// </summary>
public abstract class ForwardedJwtAudienceTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The fewest forwarded-JWT hosts the scan must find: the number of service hosts that are not the
    /// token issuer, whether they call <c>AddForwardedJwtBearer</c> directly or through the composed host
    /// (<c>AddMmcaServiceHost</c>). A floor keeps the gate non-vacuous.
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
