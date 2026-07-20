using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Asserts a host emits the hardened security response headers on every response — the header set
/// wired by the shared <c>AddCommonSecurityHeaders</c> / <c>UseCommonSecurityHeaders</c> middleware —
/// so a future pipeline refactor cannot silently drop them. Authored once here and re-run as a thin
/// subclass per host under test: the subclass supplies <see cref="CreateClient"/> from its
/// <c>WebApplicationFactory</c> class fixture (typically a gateway booted in the Production
/// environment, where HSTS is emitted). <see cref="ProbePath"/> defaults to <c>/alive</c> because the
/// liveness endpoint always responds (only the "live" self-check), independent of any backend service
/// being reachable.
/// </summary>
public abstract class SecurityHeadersTestsBase
{
    /// <summary>The always-responding path probed for headers.</summary>
    protected virtual string ProbePath => "/alive";

    [Fact]
    public async Task AliveResponse_CarriesHardenedSecurityHeaders()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(
            new Uri(ProbePath, UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(false);

        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Header(response, "Permissions-Policy").Should().Contain("geolocation=()");
        Header(response, "Content-Security-Policy").Should().Contain("frame-ancestors 'none'");
        // Production env ⇒ HSTS is emitted.
        Header(response, "Strict-Transport-Security").Should().Contain("max-age=");
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> against the booted host under test (e.g. the class
    /// fixture's <c>WebApplicationFactory.CreateClient()</c>).
    /// </summary>
    protected abstract HttpClient CreateClient();

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
