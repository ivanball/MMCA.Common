using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Web.Hardening;

/// <summary>
/// Configuration for a Blazor Web host's own edge rate limiter, bound from the
/// <c>UiRateLimiting</c> section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a UI host needs one at all.</b> The Blazor UI is a SEPARATE externally reachable origin
/// from the Gateway (its own FQDN), so the ADR-088 edge limiter that lives in the Gateway never sees
/// a single request to it. Every page load on this origin opens an interactive Server circuit, and
/// the container is typically 0.25 vCPU / 0.5 GiB, so an unauthenticated loop over the page and
/// negotiate endpoints exhausts it long before anything downstream notices.
/// </para>
/// <para>
/// <b>Shaped like the Gateway's limiter, shipped separately.</b> Same partitioning (client IP,
/// anonymous traffic included, because there is no authenticated principal at this edge either) and
/// the same pairing of a per-IP window with a replica-wide concurrency ceiling, but a distinct kit
/// rather than the one in <c>MMCA.Common.Gateway</c>: that package is the reverse-proxy kit and a
/// Blazor host is not a reverse proxy. The exemptions differ too, because this host serves
/// <c>/_framework</c> and <c>/_content</c> itself while metering the <c>/_blazor</c> negotiate that
/// opens a circuit.
/// </para>
/// <para>
/// <b>Per replica, in memory.</b> Both counts are per process, so the effective allowance is the
/// configured number multiplied by the replica count. That is the same deliberate trade the Gateway
/// kit documents: an edge limiter has to answer in microseconds on every request, and a shared
/// counter would put a network round trip in front of the whole site.
/// </para>
/// </remarks>
public sealed class UiRateLimitingSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static string SectionName => "UiRateLimiting";

    /// <summary>Gets a value indicating whether the limiter is applied at all.</summary>
    /// <remarks>
    /// An escape hatch for a load or capacity proof driven from one runner IP, which the per-IP
    /// window cannot tell from a flood. Default on: a public origin ships limited.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the number of requests one client IP may make per window, per replica.
    /// </summary>
    /// <remarks>
    /// 300/minute by default, which no real visitor approaches: a page load costs a handful of
    /// requests once static assets are exempt (they are, see
    /// <see cref="UiRateLimitingExtensions.IsExempt"/>), and Interactive Auto hands the session to
    /// the WebAssembly runtime after the first render. It is set well above a single visitor on
    /// purpose, because an office or mobile-carrier NAT presents many visitors as one IP. A host
    /// whose audience sits behind ONE address (a venue's wifi, a single corporate egress) should
    /// raise this: the whole crowd arrives as a single partition key.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; } = 300;

    /// <summary>Gets the length of the fixed window, in seconds, <see cref="PermitLimit"/> is counted over.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Gets the maximum number of requests in flight through this replica at once, across all
    /// clients. A ceiling rather than a rate, because the failure it guards against (a slow
    /// downstream backing requests up until the host runs out of threads) is not a rate problem.
    /// Excess requests are rejected immediately with 429 rather than queued, so a saturated host
    /// sheds load instead of growing latency. Unlike the per-IP window this needs no widening for a
    /// shared-address audience: in-flight concurrency is bounded by what the container can actually
    /// render, not by how many people share an address.
    /// </summary>
    [Range(1, 1_000_000)]
    public int GlobalConcurrencyLimit { get; init; } = 200;
}
