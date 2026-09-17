using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.Security;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// Pins the exact Content-Security-Policy assembled by <c>BlazorCspPolicyProvider</c> (resolved as
/// <see cref="ICspPolicyProvider"/> through <c>AddCommonBlazorCsp()</c>; the class itself is internal):
/// the enforced production policy with connect-src pinned to the configured API/Gateway origin
/// (https + matching ws scheme, port preserved, WasmApiEndpoint preferred), the fail-closed
/// <c>connect-src 'self'</c> degradation on missing/unparseable endpoints (still enforced), the
/// Development-only localhost and inline-script allowances, and the no-unsafe-eval /
/// no-inline-script-in-production regressions, and the opt-in <c>BlazorCsp:FrameSources</c> allow-list
/// (absent = byte-identical policy, configured = <c>frame-src</c> emitted, invalid = startup failure).
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

    /// <summary>The fail-closed fallback policy, pinned verbatim: identical to the production policy
    /// except that connect-src narrows to 'self'.</summary>
    private const string ExpectedFallbackPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    private static CspPolicy? GetPolicy(
        string? apiEndpoint,
        string? wasmApiEndpoint = null,
        bool isDevelopment = false,
        string[]? frameSources = null)
    {
        using var provider = BuildProvider(apiEndpoint, wasmApiEndpoint, isDevelopment, frameSources ?? []);
        return provider.GetRequiredService<ICspPolicyProvider>().GetPolicy(new DefaultHttpContext());
    }

    private static ServiceProvider BuildProvider(
        string? apiEndpoint,
        string? wasmApiEndpoint,
        bool isDevelopment,
        string[] frameSources)
    {
        var services = new ServiceCollection();
        var configValues = frameSources
            .Select((source, index) => new KeyValuePair<string, string?>(
                string.Create(CultureInfo.InvariantCulture, $"BlazorCsp:FrameSources:{index}"), source));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configValues).Build());
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

        return services.BuildServiceProvider();
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

    // == Fail-closed degradation ==
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/hosts")]
    [InlineData("ftp://gateway.example.com")]
    public void GetPolicy_WithMissingOrUnparseableEndpoint_FailsClosedOnSelfOnlyConnectSrc(string? endpoint)
    {
        var policy = GetPolicy(endpoint);

        policy.Should().NotBeNull();
        policy!.Enforce.Should().BeTrue("a security response header that stops being enforced protects nothing");

        var directives = policy.Value.Split("; ", StringSplitOptions.None);
        directives.Single(d => d.StartsWith("connect-src", StringComparison.Ordinal))
            .Should().Be("connect-src 'self'");
        policy.Value.Should().Be(ExpectedFallbackPolicy, "only connect-src narrows; the rest of the policy is unchanged");
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

    // == Frame-source allow-list ==
    [Fact]
    public void GetPolicy_WithNoFrameSources_EmitsNoFrameSrcDirective()
    {
        var policy = GetPolicy("https://api.example.com");

        policy!.Value.Should().Be(ExpectedProductionPolicy, "an empty allow-list leaves the policy byte-identical");
        policy.Value.Should().NotContain("frame-src");
    }

    [Fact]
    public void GetPolicy_WithFrameSources_EmitsFrameSrcBetweenConnectSrcAndBaseUri()
    {
        var policy = GetPolicy(
            "https://api.example.com",
            frameSources: ["https://www.google.com", "https://maps.google.com/"]);

        policy!.Enforce.Should().BeTrue();
        policy.Value.Should().Be(ExpectedProductionPolicy.Replace(
            "base-uri 'self'; ",
            "frame-src 'self' https://www.google.com https://maps.google.com; base-uri 'self'; ",
            StringComparison.Ordinal));
        policy.Value.Should().EndWith("frame-ancestors 'none'", "who may frame this host is never relaxed");
    }

    [Fact]
    public void GetPolicy_WithFrameSources_CanonicalizesAndDeduplicatesOrigins()
    {
        var policy = GetPolicy(
            "https://api.example.com",
            frameSources: ["https://Maps.Example.com:443", "https://maps.example.com", "https://embed.example.com:8443"]);

        policy!.Value.Split("; ", StringSplitOptions.None)
            .Single(d => d.StartsWith("frame-src", StringComparison.Ordinal))
            .Should().Be("frame-src 'self' https://maps.example.com https://embed.example.com:8443");
    }

    [Fact]
    public void GetPolicy_WithFrameSourcesAndFailClosedEndpoint_StillEmitsFrameSrc()
    {
        var policy = GetPolicy(null, frameSources: ["https://www.google.com"]);

        policy!.Value.Should().Contain("connect-src 'self'; frame-src 'self' https://www.google.com; base-uri 'self'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("https://*")]
    [InlineData("https://*.google.com")]
    [InlineData("https:")]
    [InlineData("www.google.com")]
    [InlineData("http://www.google.com")]
    [InlineData("data:text/html,hi")]
    [InlineData("'self'")]
    [InlineData("'unsafe-inline'")]
    [InlineData("https://www.google.com; script-src *")]
    [InlineData("https://www.google.com 'unsafe-eval'")]
    [InlineData("https://www.google.com,https://evil.example")]
    [InlineData("https://www.google.com/maps")]
    [InlineData("https://www.google.com/?q=1")]
    [InlineData("https://www.google.com/#frag")]
    [InlineData("https://www.google.com?")]
    [InlineData("https://user:pass@www.google.com")]
    public void AddCommonBlazorCsp_WithInvalidFrameSource_FailsOptionsValidationNamingTheEntry(string source)
    {
        using var provider = BuildProvider("https://api.example.com", null, false, [source]);

        var act = () => provider.GetRequiredService<ICspPolicyProvider>();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("BlazorCsp:FrameSources").And.Contain($"'{source}'");
    }

    [Fact]
    public async Task AddCommonBlazorCsp_WithInvalidFrameSource_FailsAtHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("BlazorCsp:FrameSources:0", "https://*.example.com")]);
        builder.Services.AddCommonBlazorCsp();
        using var host = builder.Build();

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact]
    public void AddCommonBlazorCsp_WithValidFrameSources_BindsThemFromTheBlazorCspSection()
    {
        using var provider = BuildProvider(
            "https://api.example.com", null, false, ["https://www.google.com", "https://maps.google.com"]);

        provider.GetRequiredService<IOptions<BlazorCspSettings>>().Value.FrameSources
            .Should().Equal("https://www.google.com", "https://maps.google.com");
    }

    // == Singleton computation ==
    [Fact]
    public void GetPolicy_IsComputedOnce_ReturnsTheSameInstanceForEveryRequest()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ApiSettings { ApiEndpoint = "https://api.example.com" }));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
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
