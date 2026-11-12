using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Auth;
using MMCA.Common.Application;
using MMCA.Common.Application.Modules;
using MMCA.Common.Aspire.Kestrel;
using MMCA.Common.Grpc.Interceptors;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.ServiceHost.Tests.Support;

namespace MMCA.Common.ServiceHost.Tests;

/// <summary>
/// <c>AddMmcaServiceHost</c> (design item 19, rules 19.1 to 19.19): the tests design 19(c) names
/// for the builder half. Each test seeds the configuration <see cref="ServiceHostTestHost"/>
/// describes and inspects registration shape, or a provider built from the collection; no test opens
/// a database, a Redis connection or an HTTP connection.
/// </summary>
public sealed class ServiceHostBuilderExtensionsTests
{
    // Rule 19.1
    [Fact]
    public void AddMmcaServiceHost_NullOptions_ThrowsArgumentNullException()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        Action act = () => builder.AddMmcaServiceHost(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // Rule 19.1
    [Fact]
    public void AddMmcaServiceHost_NullBuilder_ThrowsArgumentNullException()
    {
        WebApplicationBuilder builder = null!;

        Action act = () => builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        act.Should().Throw<ArgumentNullException>();
    }

    // Rule 19.1 (and 19.2: validation precedes the first callback)
    [Fact]
    public void AddMmcaServiceHost_EmptyModuleAssemblies_ThrowsArgumentException()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        var configurationSourcesRan = false;
        var options = ServiceHostTestHost.Options(
            configureConfigurationSources: _ => configurationSourcesRan = true,
            moduleAssemblies: []);

        Action act = () => builder.AddMmcaServiceHost(options);

        act.Should().Throw<ArgumentException>().WithMessage("*ModuleAssemblies*");
        configurationSourcesRan.Should().BeFalse("argument validation runs before any host callback");
    }

    // Rule 19.2 (before 19.5)
    [Fact]
    public void ConfigureConfigurationSources_RunsBeforeServiceDefaults()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        bool? serviceDefaultsHadRun = null;
        var options = ServiceHostTestHost.Options(
            configureConfigurationSources: b =>
                serviceDefaultsHadRun = ServiceDescriptorInspection.HasServiceDefaultsTelemetryMarker(b.Services));

        builder.AddMmcaServiceHost(options);

        serviceDefaultsHadRun.Should().BeFalse(
            "a vault-backed configuration source must be in place before AddServiceDefaults reads configuration eagerly");
    }

    // Rules 19.2 and 19.3
    [Fact]
    public void ConfigureConfigurationSources_ValueReachesHealthProbePlan()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        var options = ServiceHostTestHost.Options(
            configureConfigurationSources: b => b.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [KestrelEndpointExtensions.HealthProbePortConfigKey] = "abc",
                }));

        Action act = () => builder.AddMmcaServiceHost(options);

        act.Should().Throw<InvalidOperationException>(
                "the probe plan is computed after the configuration sources callback, so it sees the callback's non-integer port")
            .WithMessage("*HealthProbe:Port*");
    }

    // Rules 19.5 and 19.6
    [Fact]
    public void AfterServiceDefaults_RunsAfterServiceDefaults()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        bool? serviceDefaultsHadRun = null;
        var options = ServiceHostTestHost.Options(
            afterServiceDefaults: b =>
                serviceDefaultsHadRun = ServiceDescriptorInspection.HasServiceDefaultsTelemetryMarker(b.Services));

        builder.AddMmcaServiceHost(options);

        serviceDefaultsHadRun.Should().BeTrue(
            "a vendor exporter registered here must follow the filter processors AddServiceDefaults installs");
    }

    // Rules 19.2, 19.6, 19.15, 19.16, 19.17
    [Fact]
    public void Callbacks_RunInDocumentedOrder()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        var order = new List<string>();
        var options = ServiceHostTestHost.Options(
            configureConfigurationSources: _ => order.Add(nameof(MmcaServiceHostOptions.ConfigureConfigurationSources)),
            afterServiceDefaults: _ => order.Add(nameof(MmcaServiceHostOptions.AfterServiceDefaults)),
            configureServices: _ => order.Add(nameof(MmcaServiceHostOptions.ConfigureServices)),
            registerBrokerTransports: _ => order.Add(nameof(MmcaServiceHostOptions.RegisterBrokerTransports)),
            configurePipeline: (pipeline, moduleHost) =>
            {
                order.Add(nameof(MmcaServiceHostOptions.ConfigurePipeline));
                ServiceHostTestHost.RegisterModules(pipeline, moduleHost);
            });

        builder.AddMmcaServiceHost(options);

        order.Should().Equal(
            nameof(MmcaServiceHostOptions.ConfigureConfigurationSources),
            nameof(MmcaServiceHostOptions.AfterServiceDefaults),
            nameof(MmcaServiceHostOptions.ConfigureServices),
            nameof(MmcaServiceHostOptions.RegisterBrokerTransports),
            nameof(MmcaServiceHostOptions.ConfigurePipeline));
    }

    // Rules 19.14 and 19.15
    [Fact]
    public void ConfigureServices_RunsAfterAddApi()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        bool? apiRegistered = null;
        var options = ServiceHostTestHost.Options(
            configureServices: b => apiRegistered = ServiceDescriptorInspection.HasService<IdempotencyFilter>(b.Services));

        builder.AddMmcaServiceHost(options);

        apiRegistered.Should().BeTrue("AddAPI registers the IdempotencyFilter before the host's own registrations run");
    }

    // Rule 19.17
    [Fact]
    public void ConfigurePipeline_IsInvokedOnce_WithTheReturnedModuleHost()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        var received = new List<ModuleHostContext>();
        IServiceCollection? pipelineServices = null;
        var options = ServiceHostTestHost.Options(
            configurePipeline: (pipeline, moduleHost) =>
            {
                received.Add(moduleHost);
                pipelineServices = pipeline.Services;
                ServiceHostTestHost.RegisterModules(pipeline, moduleHost);
            });

        var host = builder.AddMmcaServiceHost(options);

        received.Should().ContainSingle().Which.Should().BeSameAs(host.ModuleHost);
        pipelineServices.Should().BeSameAs(builder.Services);
    }

    // Rule 19.8
    [Fact]
    public void ModuleLoaderSingleton_IsTheReturnedModuleHostLoader()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        builder.Services
            .Where(d => !d.IsKeyedService && d.ServiceType == typeof(ModuleLoader))
            .Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeSameAs(host.ModuleHost.ModuleLoader);
    }

    // Rules 19.17 and 19.19
    [Fact]
    public void AfterCall_DecoratorPipelineVerifies()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        Action act = () => builder.Services.VerifyDecoratorPipeline();

        act.Should().NotThrow("the composed call closes the ADR-014 pipeline with every handler wrapped");
    }

    // Rule 19.13
    [Fact]
    public async Task ForwardedJwtBearer_UsesConfiguredAuthority()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        await using var provider = builder.Services.BuildServiceProvider();
        var bearer = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        bearer.Authority.Should().Be(ServiceHostTestHost.Authority);
        bearer.Audience.Should().Be(ServiceHostTestHost.Audience);
    }

    // Rule 19.13
    [Fact]
    public void ForwardedJwtBearer_WithoutAuthority_Throws()
    {
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s.Remove(JwtAuthorityExtensions.JwtAuthorityConfigKey));

        Action act = () => builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Authentication:JwtBearer:Authority*");
    }

    // Rule 19.13
    [Fact]
    public async Task TokenIssuer_DoesNotRequireAuthority_AndBindsJwtSettings()
    {
        const string issuer = "https://issuer.servicehost.test";
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s =>
            {
                s.Remove(JwtAuthorityExtensions.JwtAuthorityConfigKey);
                s["Jwt:Issuer"] = issuer;
                s["Jwt:SigningAlgorithm"] = nameof(JwtSigningAlgorithm.HS256);

                // 32 bytes, Base64: long enough for the HS256 validator's 256-bit floor.
                s["Jwt:SecretForKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
            });

        Action act = () => builder.AddMmcaServiceHost(
            ServiceHostTestHost.Options(authentication: ServiceHostAuthentication.TokenIssuer));

        act.Should().NotThrow("the token issuer validates in process and reads no authority");

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<JwtSettings>>().Value.Issuer.Should().Be(issuer);
        provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .Authority.Should().BeNull("AddCommonAuthentication validates with JwtSettings, not JWKS discovery");
    }

    // Rule 19.11
    // DESIGN-QUESTION: the "NoCache base policy is added before the callback" half of 19.11 is not
    // asserted: OutputCacheOptions keeps its base policies in a non-public member, so only the
    // callback's invocation is observable from a test.
    [Fact]
    public async Task ConfigureOutputCache_IsInvokedWhenOutputCacheOptionsResolve()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        var invocations = 0;
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options(configureOutputCache: _ => invocations++));

        await using var provider = builder.Services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<OutputCacheOptions>>().Value;

        invocations.Should().Be(1);
    }

    // Rule 19.7
    [Fact]
    public void UseRedisOutputCaching_False_RegistersNoRedisOutputCacheStore()
    {
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s["ConnectionStrings:redis"] = ServiceHostTestHost.RedisConnectionString);

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options(useRedisOutputCaching: false));

        ServiceDescriptorInspection.ImplementationTypesOf<IOutputCacheStore>(builder.Services)
            .Should().NotContain(
                t => t != null && t.Name.Contains("Redis", StringComparison.Ordinal),
                "Redis-backed output caching is opt-in even when a Redis connection is configured");
    }

    // Rule 19.7
    [Fact]
    public void UseRedisOutputCaching_True_WithRedisConnection_RegistersRedisStore()
    {
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s["ConnectionStrings:redis"] = ServiceHostTestHost.RedisConnectionString);

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options(useRedisOutputCaching: true));

        ServiceDescriptorInspection.ImplementationTypesOf<IOutputCacheStore>(builder.Services)
            .Should().Contain(t => t != null && t.Name.Contains("Redis", StringComparison.Ordinal));
    }

    // Rule 19.12 (ruling 2026-11-12): the host keeps its own AddOpenApi() line, before or after the
    // composed call, and the composed call's pin holds either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenApi_IsPinnedTo31(bool hostAddsOpenApiFirst)
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        if (hostAddsOpenApiFirst)
        {
            builder.Services.AddOpenApi();
        }

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        if (!hostAddsOpenApiFirst)
        {
            builder.Services.AddOpenApi();
        }

        await using var provider = builder.Services.BuildServiceProvider();
        var openApi = provider.GetRequiredService<IOptionsMonitor<OpenApiOptions>>().Get("v1");

        openApi.OpenApiVersion.Should().Be(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
    }

    // Rule 19.18
    [Fact]
    public void GrpcServiceDefaults_AreRegistered()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        ServiceDescriptorInspection.HasService<GrpcResultExceptionInterceptor>(builder.Services).Should().BeTrue();
    }
}
