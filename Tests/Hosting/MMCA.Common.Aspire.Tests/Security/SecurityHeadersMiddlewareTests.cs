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

    // The framework default (used when a host registers no custom ICspPolicyProvider) is a COMPLETE
    // hardened baseline: script-src and style-src ship at exactly the strength Blazor ('wasm-unsafe-eval')
    // and MudBlazor ('unsafe-inline' styles) require, so an HTML host that never registers a provider
    // still gets a functional policy rather than one silently missing both directives (§26, R18).
    [Fact]
    public void DefaultSettings_ContentSecurityPolicy_IsHardenedBaseline() =>
        new SecurityHeadersSettings().ContentSecurityPolicy.Should().Be(
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
            "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'");

    // == Per-request {nonce} placeholder ==
    private static async Task<(IHeaderDictionary Headers, HttpContext Context)> RunWithContextAsync(CspPolicy? policy)
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new SecurityHeadersSettings()),
            new StubCspProvider(policy),
            new StubWebHostEnvironment(Environments.Production));

        await middleware.InvokeAsync(context);
        return (context.Response.Headers, context);
    }

    [Fact]
    public async Task InvokeAsync_PolicyWithNoncePlaceholder_SubstitutesQuotedNonceIntoTheHeader()
    {
        var (headers, context) = await RunWithContextAsync(
            new CspPolicy("script-src 'self' {nonce}; style-src 'self' {nonce}", Enforce: true));

        var nonce = CspNonce.Get(context);
        nonce.Should().NotBeNullOrWhiteSpace();
        Convert.TryFromBase64String(nonce!, new byte[nonce!.Length], out var written)
            .Should().BeTrue("the nonce is valid Base64");
        written.Should().Be(16, "the nonce carries 128 bits of entropy");

        var value = headers.ContentSecurityPolicy.ToString();
        value.Should().NotContain("{nonce}", "every occurrence of the placeholder is replaced");
        value.Should().Be(
            $"script-src 'self' 'nonce-{nonce}'; style-src 'self' 'nonce-{nonce}'",
            "one nonce is generated per request, not per occurrence, and it is emitted in quoted source-list form");
    }

    [Fact]
    public async Task InvokeAsync_PolicyWithNoncePlaceholder_StoresTheSameValueCspNonceGetReturns()
    {
        var (headers, context) = await RunWithContextAsync(new CspPolicy("script-src 'self' {nonce}", Enforce: true));

        var stored = CspNonce.Get(context);
        stored.Should().NotBeNullOrWhiteSpace();
        context.Items[CspNonce.ItemKey].Should().Be(stored);
        headers.ContentSecurityPolicy.ToString().Should().Be($"script-src 'self' 'nonce-{stored}'");
    }

    [Fact]
    public async Task InvokeAsync_PolicyWithNoncePlaceholder_GeneratesADifferentNoncePerRequest()
    {
        var policy = new CspPolicy("script-src 'self' {nonce}", Enforce: true);

        var (_, first) = await RunWithContextAsync(policy);
        var (_, second) = await RunWithContextAsync(policy);

        CspNonce.Get(second).Should().NotBe(CspNonce.Get(first));
    }

    [Fact]
    public async Task InvokeAsync_PolicyWithNoncePlaceholder_WorksInReportOnlyMode()
    {
        var (headers, context) = await RunWithContextAsync(new CspPolicy("script-src 'self' {nonce}", Enforce: false));

        var stored = CspNonce.Get(context);
        stored.Should().NotBeNullOrWhiteSpace();
        headers.ContentSecurityPolicyReportOnly.ToString().Should().Be($"script-src 'self' 'nonce-{stored}'");
        headers.ContentSecurityPolicy.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_PolicyWithoutNoncePlaceholder_EmitsTheconfiguredStringAndStoresNoNonce()
    {
        const string configured = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'";

        var (headers, context) = await RunWithContextAsync(new CspPolicy(configured, Enforce: true));

        headers.ContentSecurityPolicy.ToString().Should().Be(configured);
        context.Items.ContainsKey(CspNonce.ItemKey).Should().BeFalse();
        CspNonce.Get(context).Should().BeNull();
    }

    [Fact]
    public void CspNonce_Get_WithNoMiddlewareRun_ReturnsNull() =>
        CspNonce.Get(new DefaultHttpContext()).Should().BeNull();

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
