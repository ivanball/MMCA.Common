using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using Xunit;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Opt-in fitness function for a host's tenant resolution configuration: the shape
/// <c>TenantResolutionMiddleware</c> will run with, checked in the fast unit tier before it reaches
/// a request. Three gates, each a way a tenancy configuration fails silently in production:
/// <list type="bullet">
///   <item>Header resolution is an explicit opt-in. A caller-supplied header lets any caller name
///   any tenant, so a host whose order includes <see cref="TenantResolutionStrategy.Header"/> must
///   say so by overriding <see cref="AllowsHeaderResolution"/>.</item>
///   <item>The claim is tried before the header. The claim was signed by the token issuer; a header
///   tried first would win over it.</item>
///   <item>The health and liveness probes are excluded. A probe rejected for carrying no tenant takes
///   the replica out of rotation.</item>
/// </list>
/// <para>
/// Subclass it with the settings the host binds (typically the <c>Tenancy</c> section of its
/// <c>appsettings.json</c>, or a literal <see cref="TenancySettings"/> that mirrors it). No host is
/// built and no request is sent.
/// </para>
/// </summary>
public abstract class TenantResolutionTestsBase
{
    /// <summary>The probe paths that must bypass tenant resolution.</summary>
    private static readonly string[] ProbePaths = ["/health", "/alive"];

    /// <summary>Gets the tenancy settings the host runs with.</summary>
    protected abstract TenancySettings Settings { get; }

    /// <summary>
    /// Gets a value indicating whether this host deliberately accepts the tenant from a request
    /// header. Defaults to <see langword="false"/>; override it to return <see langword="true"/> only
    /// for a host behind a trusted gateway that sets the header itself.
    /// </summary>
    protected virtual bool AllowsHeaderResolution => false;

    /// <summary>
    /// Fails when tenancy is enabled, the resolution order includes the header, and the host has
    /// not opted in through <see cref="AllowsHeaderResolution"/>.
    /// </summary>
    [Fact]
    public void Header_resolution_is_an_explicit_opt_in()
    {
        var settings = Settings;
        if (!settings.Enabled || AllowsHeaderResolution)
        {
            return;
        }

        settings.EffectiveResolutionOrder.Should().NotContain(
            TenantResolutionStrategy.Header,
            because: "header resolution lets any caller name any tenant through the \"" + settings.HeaderName
                + "\" header, so it must be an explicit opt-in. Remove Header from Tenancy:ResolutionOrder, or, for a host "
                + "behind a trusted gateway that sets the header itself, set Tenancy:ResolutionOrder explicitly "
                + "(Claim, then Header) and override AllowsHeaderResolution to return true");
    }

    /// <summary>
    /// Fails when the resolution order includes the header without the claim before it.
    /// </summary>
    [Fact]
    public void Claim_is_tried_before_header()
    {
        var order = Settings.EffectiveResolutionOrder.ToList();
        var headerIndex = order.IndexOf(TenantResolutionStrategy.Header);
        if (headerIndex < 0)
        {
            return;
        }

        var claimIndex = order.IndexOf(TenantResolutionStrategy.Claim);

        (claimIndex >= 0 && claimIndex < headerIndex).Should().BeTrue(
            because: "the signed claim must win over the caller-supplied header, so Tenancy:ResolutionOrder must list "
                + "Claim before Header (configured order: " + string.Join(", ", order) + ")");
    }

    /// <summary>
    /// Fails when the effective excluded prefixes no longer cover <c>/health</c> and <c>/alive</c>,
    /// matched the way the middleware matches them (segment prefix, case-insensitive).
    /// </summary>
    [Fact]
    public void Health_endpoints_are_excluded()
    {
        var prefixes = Settings.EffectiveExcludedPathPrefixes;
        var uncovered = ProbePaths
            .Where(probe => !prefixes.Any(prefix =>
                new PathString(probe).StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        uncovered.Should().BeEmpty(
            because: "a probe rejected for carrying no tenant takes the replica out of rotation. Add the missing paths to "
                + "Tenancy:ExcludedPathPrefixes, or leave that list empty to keep the framework default (configured: "
                + string.Join(", ", prefixes) + ")");
    }
}
