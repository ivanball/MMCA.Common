using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Common.Settings;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// Pins the exact Content-Security-Policy assembled by <c>BlazorCspPolicyProvider</c> (resolved as
/// <see cref="ICspPolicyProvider"/> through <c>AddCommonBlazorCsp()</c>; the class itself is internal):
/// the enforced production policy with connect-src pinned to the configured API/Gateway origin
/// (https + matching ws scheme, port preserved, WasmApiEndpoint preferred), the permissive
/// Report-Only degradation on missing/unparseable endpoints, the Development-only localhost and
/// inline-script allowances, and the no-unsafe-eval / no-inline-script-in-production regressions.
/// </summary>
public sealed class BlazorCspPolicyProviderTests
{
    /// <summary>The full enforced policy for an https endpoint with no explicit port, pinned verbatim.</summary>
    private const string ExpectedProductionPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        "connect-src 'self' https://api.example.com wss://api.example.com; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>The permissive Report-Only fallback policy, pinned verbatim.</summary>
    private const string ExpectedFallbackPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        "connect-src 'self' https: wss:; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    private static CspPolicy? GetPolicy(
        string? apiEndpoint,
        string? wasmApiEndpoint = null,
        bool isDevelopment = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ApiSettings
        {
            ApiEndpoint = apiEndpoint,
            WasmApiEndpoint = wasmApiEndpoint,
        }));
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName)
            .Returns(isDevelopment ? Environments.Development : Environments.Production);
        services.AddSingleton(environment.Object);
        services.AddCommonBlazorCsp();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ICspPolicyProvider>().GetPolicy(new DefaultHttpContext());
    }

    // == Enforced production policy ==
    [Fact]
    public void GetPolicy_WithHttpsApiEndpoint_PinsExactEnforcedProductionPolicy()
    {
        var policy = GetPolicy("https://api.example.com");

        policy.Should().NotBeNull();
        policy!.Enforce.Should().BeTrue();
        policy.Value.Should().Be(ExpectedProductionPolicy);
    }

    [Fact]
    public void GetPolicy_WithWasmApiEndpointConfigured_PrefersItOverApiEndpoint()
    {
        var policy = GetPolicy("https://api.example.com", wasmApiEndpoint: "https://gateway.example.com");

        policy!.Value.Should().Contain("connect-src 'self' https://gateway.example.com wss://gateway.example.com");
        policy.Value.Should().NotContain("api.example.com");
    }

    [Fact]
    public void GetPolicy_WithHttpEndpoint_UsesWsSchemeForTheSocketOrigin()
    {
        var policy = GetPolicy("http://gateway:8080");

        policy!.Enforce.Should().BeTrue();
        policy.Value.Should().Contain("connect-src 'self' http://gateway:8080 ws://gateway:8080");
    }

    [Fact]
    public void GetPolicy_WithNonDefaultHttpsPort_PreservesThePort()
    {
        var policy = GetPolicy("https://gateway.example.com:8443/api/");

        policy!.Value.Should().Contain(
            "connect-src 'self' https://gateway.example.com:8443 wss://gateway.example.com:8443");
    }

    // == Report-Only degradation ==
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/hosts")]
    [InlineData("ftp://gateway.example.com")]
    public void GetPolicy_WithMissingOrUnparseableEndpoint_DegradesToPermissiveReportOnly(string? endpoint)
    {
        var policy = GetPolicy(endpoint);

        policy.Should().NotBeNull();
        policy!.Enforce.Should().BeFalse("a CSP we cannot construct correctly must never be enforced");
        policy.Value.Should().Be(ExpectedFallbackPolicy);
    }

    // == Development-only allowances ==
    [Fact]
    public void GetPolicy_InDevelopment_AllowsLocalhostWebSocketsAndInlineBootstrapScript()
    {
        var policy = GetPolicy("https://api.example.com", isDevelopment: true);

        policy!.Enforce.Should().BeTrue();
        policy.Value.Should().Contain("script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline'; ");
        policy.Value.Should().Contain(
            "connect-src 'self' https://api.example.com wss://api.example.com http://localhost:* ws://localhost:*");
    }

    // == Hardening regressions ==
    [Fact]
    public void GetPolicy_NeverContainsUnsafeEval()
    {
        GetPolicy("https://api.example.com")!.Value.Should().NotContain("'unsafe-eval'");
        GetPolicy("https://api.example.com", isDevelopment: true)!.Value.Should().NotContain("'unsafe-eval'");
        GetPolicy(null)!.Value.Should().NotContain("'unsafe-eval'");
    }

    [Fact]
    public void GetPolicy_InProduction_AllowsInlineOnlyForStyles()
    {
        var directives = GetPolicy("https://api.example.com")!.Value.Split("; ", StringSplitOptions.None);

        directives.Single(d => d.StartsWith("script-src", StringComparison.Ordinal))
            .Should().Be("script-src 'self' 'wasm-unsafe-eval'");
        directives.Single(d => d.StartsWith("style-src", StringComparison.Ordinal))
            .Should().Be("style-src 'self' 'unsafe-inline'");
    }

    // == Singleton computation ==
    [Fact]
    public void GetPolicy_IsComputedOnce_ReturnsTheSameInstanceForEveryRequest()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ApiSettings { ApiEndpoint = "https://api.example.com" }));
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        services.AddSingleton(environment.Object);
        services.AddCommonBlazorCsp();
        using var provider = services.BuildServiceProvider();
        var sut = provider.GetRequiredService<ICspPolicyProvider>();

        var first = sut.GetPolicy(new DefaultHttpContext());
        var second = sut.GetPolicy(new DefaultHttpContext());

        second.Should().BeSameAs(first);
    }
}
