using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Security;

namespace MMCA.Common.Aspire.Tests.Security;

/// <summary>
/// Unit tests for <see cref="SecurityHeadersMiddleware"/>: the hardened baseline headers are always
/// emitted, HSTS is environment-gated, and the Content-Security-Policy follows the injected
/// <see cref="ICspPolicyProvider"/> (enforced / Report-Only / none).
/// </summary>
public sealed class SecurityHeadersMiddlewareTests
{
    private static async Task<IHeaderDictionary> RunAsync(
        SecurityHeadersSettings settings,
        ICspPolicyProvider cspProvider,
        string environmentName)
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            Options.Create(settings),
            cspProvider,
            new StubWebHostEnvironment(environmentName));

        await middleware.InvokeAsync(context);
        return context.Response.Headers;
    }

    [Fact]
    public async Task InvokeAsync_AlwaysSetsBaselineHeaders()
    {
        var headers = await RunAsync(new SecurityHeadersSettings(), new StubCspProvider(null), Environments.Production);

        headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        headers.XFrameOptions.ToString().Should().Be("DENY");
        headers["Referrer-Policy"].ToString().Should().Be("strict-origin-when-cross-origin");
        headers["Permissions-Policy"].ToString().Should().Contain("geolocation=()");
    }

    [Fact]
    public async Task InvokeAsync_InProduction_WithHstsEnabled_SetsHsts()
    {
        var headers = await RunAsync(new SecurityHeadersSettings { EnableHsts = true }, new StubCspProvider(null), Environments.Production);

        headers.StrictTransportSecurity.ToString().Should().Contain("max-age=31536000");
    }

    [Fact]
    public async Task InvokeAsync_InDevelopment_DoesNotSetHsts()
    {
        var headers = await RunAsync(new SecurityHeadersSettings { EnableHsts = true }, new StubCspProvider(null), Environments.Development);

        headers.StrictTransportSecurity.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_EnforcedCsp_SetsContentSecurityPolicy()
    {
        var headers = await RunAsync(
            new SecurityHeadersSettings(),
            new StubCspProvider(new CspPolicy("frame-ancestors 'none'", Enforce: true)),
            Environments.Production);

        headers.ContentSecurityPolicy.ToString().Should().Be("frame-ancestors 'none'");
        headers.ContentSecurityPolicyReportOnly.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ReportOnlyCsp_SetsReportOnlyHeader()
    {
        var headers = await RunAsync(
            new SecurityHeadersSettings(),
            new StubCspProvider(new CspPolicy("default-src 'self'", Enforce: false)),
            Environments.Production);

        headers.ContentSecurityPolicyReportOnly.ToString().Should().Be("default-src 'self'");
        headers.ContentSecurityPolicy.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_NullCsp_SetsNoCspHeader()
    {
        var headers = await RunAsync(new SecurityHeadersSettings(), new StubCspProvider(null), Environments.Production);

        headers.ContentSecurityPolicy.ToString().Should().BeEmpty();
        headers.ContentSecurityPolicyReportOnly.ToString().Should().BeEmpty();
    }

    // The framework default (used when a host registers no custom ICspPolicyProvider) must be a
    // conservative hardened baseline — safe for API/Gateway JSON responses, and deliberately omitting
    // script-src/style-src so an HTML host that forgot a provider isn't broken (§26, R18).
    [Fact]
    public void DefaultSettings_ContentSecurityPolicy_IsHardenedBaseline() =>
        new SecurityHeadersSettings().ContentSecurityPolicy.Should().Be(
            "default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'");

    private sealed class StubCspProvider(CspPolicy? policy) : ICspPolicyProvider
    {
        public CspPolicy? GetPolicy(HttpContext context) => policy;
    }

    private sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
