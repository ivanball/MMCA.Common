using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization.Fallback;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Web.Hardening;
using MMCA.Common.UI.Web.Hosting;
using MMCA.Common.UI.Web.Security;
using MMCA.Common.UI.Web.Services;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Specifies <c>AddMmcaBlazorWebHost</c> (item 20, rules 1 to 15): the composed registration a Blazor
/// head calls instead of its own copy of the shared list. The callbacks are asserted by WHERE they run
/// in the composition (before or after service defaults, immediately after the server components),
/// and the shared registrations by what wins at resolution time.
/// </summary>
public sealed class BlazorWebHostExtensionsTests
{
    /// <summary>Rule 20.1.</summary>
    [Fact]
    public void AddMmcaBlazorWebHost_NullOptions_Throws()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        var act = () => builder.AddMmcaBlazorWebHost(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Rule 20.8: the text both heads throw today, kept verbatim.</summary>
    [Fact]
    public void MissingApiEndpoint_ThrowsWithTheDocumentedMessage()
    {
        var builder = BlazorWebHostHarness.CreateBuilder(withApiEndpoint: false);

        var act = () => builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Api:ApiEndpoint configuration is required for the cookie session refresher.");
    }

    /// <summary>
    /// Rule 20.2: the configuration-source callback runs first, before <c>AddServiceDefaults()</c>
    /// reads configuration eagerly, so a vault-only value is seen there too.
    /// </summary>
    [Fact]
    public void ConfigureConfigurationSources_RunsBeforeServiceDefaults()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        WebApplicationBuilder? received = null;
        bool? markerPresent = null;
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            ConfigureConfigurationSources = b =>
            {
                received = b;
                markerPresent = BlazorWebHostHarness.HasServiceDefaultsMarker(b.Services);
            },
        };

        builder.AddMmcaBlazorWebHost(options);

        received.Should().BeSameAs(builder);
        markerPresent.Should().BeFalse("the configuration sources load before AddServiceDefaults() runs");
        BlazorWebHostHarness.HasServiceDefaultsMarker(builder.Services).Should().BeTrue();
    }

    /// <summary>
    /// Rules 20.2 and 20.8 together: a value only the added source carries (the Key Vault case) is
    /// visible to the composed call's own reads, so the required API endpoint may live in the vault.
    /// </summary>
    [Fact]
    public void ConfigureConfigurationSources_ValuesAreVisibleToTheComposedCall()
    {
        var builder = BlazorWebHostHarness.CreateBuilder(withApiEndpoint: false);
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            ConfigureConfigurationSources = b => b.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Api:ApiEndpoint"] = BlazorWebHostHarness.ApiEndpoint,
                    ["Api:WasmApiEndpoint"] = BlazorWebHostHarness.WasmApiEndpoint,
                }),
        };

        var act = () => builder.AddMmcaBlazorWebHost(options);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Rule 20.3: the after-service-defaults callback runs once the service-defaults receipt exists,
    /// which is what <c>AddAzureMonitorExporter()</c> refuses to run without.
    /// </summary>
    [Fact]
    public void AfterServiceDefaults_RunsAfterServiceDefaults()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        var calls = 0;
        bool? markerPresent = null;
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            AfterServiceDefaults = b =>
            {
                calls++;
                b.Should().BeSameAs(builder);
                markerPresent = BlazorWebHostHarness.HasServiceDefaultsMarker(b.Services);
            },
        };

        builder.AddMmcaBlazorWebHost(options);

        calls.Should().Be(1);
        markerPresent.Should().BeTrue("AddServiceDefaults() has registered its receipt before the callback runs");
    }

    /// <summary>
    /// Rule 20.4: the Razor components callback runs once, with the builder the server components were
    /// added to, immediately after them (the server circuit options are configured, the bounded-circuit
    /// handler of rule 20.5 is not registered yet).
    /// </summary>
    [Fact]
    public void ConfigureRazorComponents_IsInvokedOnce_WithTheRazorBuilder()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        var calls = 0;
        IServiceCollection? razorServices = null;
        bool? serverComponentsAdded = null;
        bool? boundedCircuitsAdded = null;
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            ConfigureRazorComponents = rc =>
            {
                calls++;
                razorServices = rc.Services;
                serverComponentsAdded = rc.Services.Any(d => d.ServiceType == typeof(IConfigureOptions<CircuitOptions>));
                boundedCircuitsAdded = rc.Services.Any(IsBoundedCircuitHandler);
            },
        };

        builder.AddMmcaBlazorWebHost(options);

        calls.Should().Be(1);
        razorServices.Should().BeSameAs(builder.Services);
        serverComponentsAdded.Should().BeTrue("AddInteractiveServerComponents runs before the callback");
        boundedCircuitsAdded.Should().BeFalse("the callback runs immediately, before AddBoundedBlazorCircuits");
        builder.Services.Should().Contain(descriptor => IsBoundedCircuitHandler(descriptor));
    }

    /// <summary>Rule 20.5: both pieces of the own-host UI hardening are registered.</summary>
    [Fact]
    public async Task RegistersTheBoundedCircuitsAndTheUiRateLimiter()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());

        builder.Services.Should().Contain(descriptor => IsBoundedCircuitHandler(descriptor));
        await using var app = builder.Build();
        var limiter = app.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        limiter.RejectionStatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
        limiter.GlobalLimiter.Should().NotBeNull();
    }

    /// <summary>Rule 20.6: the MudBlazor callback reaches <c>AddMudServices</c>.</summary>
    [Fact]
    public void ConfigureMudServices_IsInvoked()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        var calls = 0;
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            ConfigureMudServices = config =>
            {
                calls++;
                config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
            },
        };

        builder.AddMmcaBlazorWebHost(options);

        // AddMudServices(Action<MudServicesConfiguration>) applies the callback while registering,
        // copying the values onto the snackbar and dialog options it adds.
        calls.Should().Be(1);
        builder.Services.Should().Contain(d => d.ServiceType == typeof(MudBlazor.ISnackbar));
    }

    /// <summary>Rule 20.6: with no callback the MudBlazor services are still registered, with defaults.</summary>
    [Fact]
    public void ConfigureMudServices_Null_StillRegistersMudServices()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());

        builder.Services.Should().Contain(d => d.ServiceType == typeof(MudBlazor.ISnackbar));
    }

    /// <summary>Rule 20.11: the fallback-authorization callback reaches the bound options.</summary>
    [Fact]
    public async Task ConfigureFallbackAuthorization_IsInvoked()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        var options = new MmcaBlazorWebHostOptions
        {
            LogFilePath = BlazorWebHostHarness.LogFilePath,
            ConfigureFallbackAuthorization = fallback => fallback.ExemptPathPrefixes.Add("/favicon.svg"),
        };

        builder.AddMmcaBlazorWebHost(options);
        await using var app = builder.Build();

        var fallbackOptions = app.Services.GetRequiredService<IOptions<FallbackAuthorizationOptions>>().Value;
        fallbackOptions.ExemptPathPrefixes.Should().Contain("/favicon.svg");
        fallbackOptions.Enabled.Should().BeTrue("the callback extends the policy, it does not switch it off");
    }

    /// <summary>
    /// Rule 20.14: the dynamic Blazor CSP provider is registered before <c>AddCommonSecurityHeaders</c>,
    /// whose static default is a TryAdd and so never displaces it.
    /// </summary>
    [Fact]
    public async Task CspProvider_ResolvesToBlazorCspPolicyProvider()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());

        builder.Services.Last(d => d.ServiceType == typeof(ICspPolicyProvider))
            .ImplementationType.Should().Be<BlazorCspPolicyProvider>();
        await using var app = builder.Build();
        app.Services.GetRequiredService<ICspPolicyProvider>().Should().BeOfType<BlazorCspPolicyProvider>();
    }

    /// <summary>Rule 20.10: the session-cookie scheme is the default authentication scheme.</summary>
    [Fact]
    public async Task DefaultAuthenticationScheme_IsSessionCookie()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());
        await using var app = builder.Build();

        var authentication = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        authentication.DefaultScheme.Should().Be(SessionCookieAuthenticationHandler.SchemeName);
        authentication.Schemes.Should().Contain(s => string.Equals(s.Name, SessionCookieAuthenticationHandler.SchemeName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rule 20.13: the last registration of each contract is the server head's implementation, so it
    /// is the one the container resolves.
    /// </summary>
    [Fact]
    public void FormFactor_IsWebFormFactor_AndStateProvider_IsJwtAuthenticationStateProvider()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());

        builder.Services.Last(d => d.ServiceType == typeof(IFormFactor))
            .ImplementationType.Should().Be<WebFormFactor>();
        builder.Services.Last(d => d.ServiceType == typeof(AuthenticationStateProvider))
            .ImplementationType.Should().Be<JwtAuthenticationStateProvider>();
    }

    /// <summary>
    /// Rule 20.15: <c>AddUIShared</c> TryAdds the default OAuth settings, so a head (ADC) that registers
    /// its own after the composed call wins.
    /// </summary>
    [Fact]
    public async Task LaterOAuthUiSettingsRegistration_Wins()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        var appSettings = new Mock<IOAuthUISettings>().Object;

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());
        builder.Services.AddSingleton(appSettings);
        await using var app = builder.Build();

        app.Services.GetRequiredService<IOAuthUISettings>().Should().BeSameAs(appSettings);
    }

    private static bool IsBoundedCircuitHandler(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(CircuitHandler)
        && descriptor.ImplementationType == typeof(BoundedCircuitHandler);
}
