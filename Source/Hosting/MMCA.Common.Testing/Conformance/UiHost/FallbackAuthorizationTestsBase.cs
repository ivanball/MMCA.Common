using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization.Fallback;
using Xunit;

namespace MMCA.Common.Testing.Conformance.UiHost;

/// <summary>
/// Pins a Blazor UI host's adoption of the framework fallback authorization policy: an endpoint that
/// declares no authorization requires an authenticated caller, so a forgotten gate fails closed. What
/// is pinned here is the part every host must answer the same way: every endpoint in the table the
/// host actually built declares a decision or sits under an exempt prefix, the framework's shared SSR
/// <c>/Error</c> page is not exempted by path (the page declares <c>[AllowAnonymous]</c> itself), and
/// an anonymous request to each of the host's public paths is neither refused (401) nor forbidden (403).
/// </summary>
/// <remarks>
/// The registration assertion alone would pass on a host that adopted the policy and locked itself
/// out, so the sweep walks the real endpoint table and the theory drives real anonymous traffic.
/// A consumer keeps a small sealed subclass (the ADR-015 pattern) that implements
/// <c>IClassFixture</c> over its own UI host factory, returns it from <see cref="Factory"/>, and
/// declares the public paths as <c>public static TheoryData&lt;string&gt; PublicPaths</c> (the member
/// <see cref="AnonymousRequest_ToAPublicPath_IsNotRefused"/> reads, see
/// <see cref="PublicPathsMemberName"/>). App-specific facts (a static file list, the session-cookie
/// seeding endpoint, a re-executed error page) stay in the subclass and can reuse
/// <see cref="IsExempt"/>.
/// <para>
/// The public paths are deliberately limited to paths the host can answer on its own: a page that
/// fetches from the Gateway while it renders cannot be driven to its content in-process, so only the
/// authorization outcome is asserted.
/// </para>
/// </remarks>
/// <typeparam name="TEntryPoint">The UI host's entry-point class, typically its <c>Program</c>.</typeparam>
public abstract class FallbackAuthorizationTestsBase<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>
    /// The name of the static member the sealed subclass declares to supply the anonymous public
    /// paths: <c>public static TheoryData&lt;string&gt; PublicPaths</c>. xUnit resolves it on the
    /// concrete test class, so each host supplies its own rows; a subclass that omits it fails
    /// discovery of the theory loudly rather than running zero cases.
    /// </summary>
    public const string PublicPathsMemberName = "PublicPaths";

    /// <summary>
    /// The catch-all the static-asset middleware registers for itself to serve a file that is not in
    /// the build manifest. It is the framework's own endpoint, added beside the manifest endpoints
    /// rather than through the builder <c>MapStaticAssets()</c> returns, so the host cannot chain a
    /// convention onto it. It matches file paths only and serves bytes off disk; a host proves the
    /// behaviour it governs by listing a root-level static file among its public paths.
    /// </summary>
    protected const string StaticAssetFallbackRoute = "/{**path:file}";

    /// <summary>Gets the booted UI host under test, typically the xUnit class fixture's factory.</summary>
    protected abstract WebApplicationFactory<TEntryPoint> Factory { get; }

    /// <summary>
    /// The host adds no <c>/Error</c> path exemption: the framework's shared SSR error page declares
    /// <c>[AllowAnonymous]</c> itself, so a path carve-out would only widen the exemption to anything
    /// else mapped under <c>/Error</c>.
    /// </summary>
    [Fact]
    public void Host_DoesNotExemptTheErrorPageByPath()
    {
        using var client = Factory.CreateClient();

        var fallback = Factory.Services.GetRequiredService<IOptions<FallbackAuthorizationOptions>>().Value;

        fallback.ExemptPathPrefixes.Should().NotContain(
            "/Error",
            "the framework error page declares [AllowAnonymous] itself, so a path carve-out would only widen the exemption to anything else mapped under /Error");
    }

    /// <summary>
    /// Every endpoint the host maps has to be accounted for: it declares an authorization decision of
    /// its own, or its path sits under an exempt prefix. Anything else is gated by the fallback
    /// policy, and on a public site that means a 401 nobody wrote.
    /// </summary>
    /// <remarks>
    /// The deterministic half of the suite. It reads the endpoint table the host actually built
    /// (Blazor pages, static assets, the framework client-config, session-cookie, culture and health
    /// endpoints, the same-origin proxy, and the host's own minimal APIs), so an endpoint added later
    /// with no decision fails here rather than in production.
    /// </remarks>
    [Fact]
    public void EveryMappedEndpoint_DeclaresADecisionOrSitsUnderAnExemptPrefix()
    {
        using var client = Factory.CreateClient();

        var fallback = Factory.Services.GetRequiredService<IOptions<FallbackAuthorizationOptions>>().Value;
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var undecided = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Where(path => !IsExempt(fallback, path) && !string.Equals(path, StaticAssetFallbackRoute, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        undecided.Should().BeEmpty(
            "an endpoint with no authorization decision is gated by the fallback policy, so a public one answers 401 to every visitor; undecided: {0}",
            string.Join("; ", undecided));
    }

    /// <summary>
    /// Endpoints a caller reaches before authenticating, one case per row of the subclass's
    /// <c>PublicPaths</c>. A 401 or 403 on any of these is the site refusing to serve anyone who is
    /// not already signed in.
    /// </summary>
    /// <param name="path">The anonymous path.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Theory]
    [MemberData(PublicPathsMemberName)]
    public async Task AnonymousRequest_ToAPublicPath_IsNotRefused(string path)
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(false);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "{0} is public, so the fallback policy must not gate it",
            path);
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "{0} is public, so the fallback policy must not gate it",
            path);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of the fallback policy's exempt prefixes or sits beneath
    /// one (a segment match: <c>/health</c> exempts <c>/health/ready</c> but not <c>/healthz</c>).
    /// </summary>
    /// <param name="fallback">The host's bound fallback options.</param>
    /// <param name="path">The rooted route path.</param>
    /// <returns><see langword="true"/> when the policy does not gate the path.</returns>
    protected static bool IsExempt(FallbackAuthorizationOptions fallback, string path)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(path);

        return fallback.ExemptPathPrefixes.Any(prefix =>
            string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }
}
