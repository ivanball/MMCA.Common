using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Auth;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
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

    // ── Composition rules ──
    // The AddMmcaServiceHost behaviour rules (19.1 to 19.19) that the tests named in design 19(c)
    // leave unpinned: each test below observes one owned call or one ordering constraint through
    // registration shape, a provider built from the collection, or a callback that inspects the
    // collection at the moment it runs.
    // 19(a): the option defaults are the REST/gRPC service profile.
    [Fact]
    public void Options_Defaults_AreTheHttp2ServiceProfile()
    {
        var options = new MmcaServiceHostOptions
        {
            LogFilePath = ServiceHostTestHost.LogFilePath(),
            ModuleAssemblies = ServiceHostTestHost.ModuleAssemblies,
            ConfigurePipeline = ServiceHostTestHost.RegisterModules,
        };

        options.Protocols.Should().Be(HttpProtocols.Http2);
        options.RedeclareCleartextEndpoint.Should().BeTrue();
        options.Authentication.Should().Be(ServiceHostAuthentication.ForwardedJwtBearer);
        options.UseRedisOutputCaching.Should().BeFalse();
        options.ConfigureOutputCache.Should().BeNull();
        options.ConfigureConfigurationSources.Should().BeNull();
        options.AfterServiceDefaults.Should().BeNull();
        options.ConfigureServices.Should().BeNull();
        options.RegisterBrokerTransports.Should().BeNull();
        ((int)ServiceHostAuthentication.ForwardedJwtBearer).Should().Be(0);
        ((int)ServiceHostAuthentication.TokenIssuer).Should().Be(1);
    }

    // Rule 19.2: the callback's source is in place before the eager binds (authority, health checks).
    [Fact]
    public async Task ConfigureConfigurationSources_ValueReachesEagerBinds()
    {
        const string vaultAuthority = "https://identity-from-vault.servicehost.test";
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s.Remove(JwtAuthorityExtensions.JwtAuthorityConfigKey));
        var options = ServiceHostTestHost.Options(
            configureConfigurationSources: b => b.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [JwtAuthorityExtensions.JwtAuthorityConfigKey] = vaultAuthority,
                }));

        builder.AddMmcaServiceHost(options);

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .Authority.Should().Be(vaultAuthority);
    }

    // Rule 19.4 (before 19.5): Serilog joins the logging providers ahead of the OpenTelemetry one
    // AddServiceDefaults adds, which is the contract AddCommonSerilog documents.
    // DESIGN-QUESTION: no callback runs between steps 4 and 5, so the order is read from the resolved
    // ILoggerProvider sequence (registration order); provider types are matched by name because the
    // test project takes no direct Serilog or OpenTelemetry reference.
    [Fact]
    public async Task Serilog_IsRegisteredBeforeTheServiceDefaultsLoggingProvider()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        await using var provider = builder.Services.BuildServiceProvider();
        var providerNames = provider.GetServices<ILoggerProvider>().Select(p => p.GetType().Name).ToList();

        var serilog = providerNames.FindIndex(n => n.Contains("Serilog", StringComparison.Ordinal));
        var openTelemetry = providerNames.FindIndex(n => n.Contains("OpenTelemetry", StringComparison.Ordinal));
        serilog.Should().BeGreaterThanOrEqualTo(0, "AddCommonSerilog registers Serilog as one logging provider");
        openTelemetry.Should().BeGreaterThan(serilog, "AddCommonSerilog runs before AddServiceDefaults");
    }

    // Rule 19.7: Redis caching and the two-level cache are both owned by the call.
    [Fact]
    public void RedisConnection_RegistersTheRedisClientAndTheHybridTier()
    {
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s["ConnectionStrings:redis"] = ServiceHostTestHost.RedisConnectionString);

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        ServiceDescriptorInspection.HasServiceNamed(builder.Services, "IConnectionMultiplexer")
            .Should().BeTrue("AddRedisCaching registers the multiplexer when the redis connection string is set");
        ServiceDescriptorInspection.HasServiceNamed(builder.Services, "HybridCache")
            .Should().BeTrue("AddCommonHybridCacheWhenRedisConfigured adds the two-level cache over Redis");
    }

    // Rule 19.7: without Redis both calls are no-ops.
    [Fact]
    public void NoRedisConnection_RegistersNoRedisClientAndNoHybridTier()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        ServiceDescriptorInspection.HasServiceNamed(builder.Services, "IConnectionMultiplexer").Should().BeFalse();
        ServiceDescriptorInspection.HasServiceNamed(builder.Services, "HybridCache").Should().BeFalse();
    }

    // Rule 19.8: the loader logs through the bootstrap factory, and the factory is kept on the result.
    [Fact]
    public void ModuleLoader_LogsThroughTheBootstrapLoggerFactory()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        host.BootstrapLoggerFactory.Should().NotBeNull();
        host.ModuleHost.ModuleLoader.Logger.Should().NotBeSameAs(
            NullLogger<ModuleLoader>.Instance,
            "AddModuleHost receives a logger created from the bootstrap factory");
    }

    // Rule 19.9: requireDatabase is true.
    [Fact]
    public void NoDatabaseConfigured_Throws()
    {
        var builder = ServiceHostTestHost.CreateBuilder(
            adjustSettings: s => s.Remove("DataSources:Test:SQLServerConnectionString"));

        Action act = () => builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        act.Should().Throw<InvalidOperationException>().WithMessage("*No database connection string is configured*");
    }

    // Rules 19.9 and 19.18: the database check and the module check both reach the health registry.
    [Fact]
    public async Task HealthChecks_IncludeTheDatabaseAndTheDiscoveredModule()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        await using var provider = builder.Services.BuildServiceProvider();
        var names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(r => r.Name)
            .ToList();

        names.Should().Contain("sqlserver");
        names.Should().Contain(
            $"module-{ServiceHostTestModule.ModuleName}",
            "AddModuleHealthChecks runs after the pipeline, so it sees the module discovery found");
    }

    // Rule 19.10: CORS policies and the configuration overload of rate limiting.
    [Fact]
    public async Task CorsAndRateLimiting_AreRegistered_FromConfiguration()
    {
        var builder = ServiceHostTestHost.CreateBuilder(adjustSettings: s => s["RateLimiting:GlobalPermitLimit"] = "77");
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        await using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<CorsOptions>>().Value
            .GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins)
            .Should().NotBeNull();
        provider.GetRequiredService<IOptions<RateLimitingSettings>>().Value.GlobalPermitLimit
            .Should().Be(77, "only the configuration overload of AddCommonRateLimiting binds the RateLimiting section");
    }

    // Rule 19.10: API versioning reads the api-version header.
    [Fact]
    public async Task ApiVersioning_IsRegistered()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        await using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value.ApiVersionReader
            .Should().BeOfType<Asp.Versioning.HeaderApiVersionReader>();
    }

    // Rule 19.12: response compression is owned by the call.
    [Fact]
    public void ResponseCompression_IsRegistered()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        ServiceDescriptorInspection.HasService<IResponseCompressionProvider>(builder.Services).Should().BeTrue();
    }

    // Rule 19.13: a missing audience fails exactly as the hand-written hosts do.
    [Fact]
    public void ForwardedJwtBearer_WithoutAudience_Throws()
    {
        var builder = ServiceHostTestHost.CreateBuilder(adjustSettings: s => s.Remove(JwtAudience.ConfigKey));

        Action act = () => builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:Audience*");
    }

    // Rule 19.13, the other half: the token issuer validates in process and reads neither the
    // forwarded audience nor the authority at registration, so the architecture rule that counts
    // composed hosts rightly leaves the issuer out.
    [Fact]
    public void TokenIssuer_WithoutAudience_DoesNotThrow()
    {
        var builder = ServiceHostTestHost.CreateBuilder(adjustSettings: s =>
        {
            s.Remove(JwtAudience.ConfigKey);
            s.Remove(JwtAuthorityExtensions.JwtAuthorityConfigKey);
        });

        Action act = () => builder.AddMmcaServiceHost(
            ServiceHostTestHost.Options(authentication: ServiceHostAuthentication.TokenIssuer));

        act.Should().NotThrow("the forwarded audience guard runs only on the ForwardedJwtBearer path");
    }

    // Rules 19.14 and 19.15: exception handlers and infrastructure precede the host's registrations,
    // and the pipeline is still open while they run.
    [Fact]
    public void ConfigureServices_RunsAfterInfrastructureAndExceptionHandlers_BeforeThePipeline()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        bool? exceptionHandlers = null;
        bool? infrastructure = null;
        Exception? verifyWhileRunning = null;
        var options = ServiceHostTestHost.Options(
            configureServices: b =>
            {
                exceptionHandlers = ServiceDescriptorInspection.HasService<IExceptionHandler>(b.Services);
                infrastructure = ServiceDescriptorInspection.HasService<IUnitOfWork>(b.Services);
                verifyWhileRunning = Record.Exception(() => b.Services.VerifyDecoratorPipeline());
            });

        builder.AddMmcaServiceHost(options);

        exceptionHandlers.Should().BeTrue("AddCommonExceptionHandlers runs before ConfigureServices");
        infrastructure.Should().BeTrue("AddInfrastructure runs before ConfigureServices");
        verifyWhileRunning.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("never closed", "the decorator pipeline is still open for app registrations");
    }

    // Rule 19.16: transports are registered on the host's own collection while the pipeline is open.
    [Fact]
    public void RegisterBrokerTransports_ReceivesTheBuilderServices_BeforeThePipelineCloses()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        IServiceCollection? received = null;
        Exception? verifyWhileRunning = null;
        var options = ServiceHostTestHost.Options(
            registerBrokerTransports: services =>
            {
                received = services;
                verifyWhileRunning = Record.Exception(() => services.VerifyDecoratorPipeline());
            });

        builder.AddMmcaServiceHost(options);

        received.Should().BeSameAs(builder.Services);
        verifyWhileRunning.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("never closed", "a transport must exist before AddBrokerMessaging runs inside the pipeline");
    }

    // Rule 19.17: the pipeline body runs between AddApplication and the decorators, so discovery lands.
    [Fact]
    public void ConfigurePipeline_ModuleDiscoveryRegistersTheModule()
    {
        var builder = ServiceHostTestHost.CreateBuilder();

        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        ServiceDescriptorInspection.HasService<ServiceHostTestModuleMarker>(builder.Services).Should().BeTrue();
        host.ModuleHost.ModuleLoader.EnabledModules.Select(m => m.Name)
            .Should().ContainSingle().Which.Should().Be(ServiceHostTestModule.ModuleName);
    }

    // Rule 19.19: after the call the sealed pipeline rejects a later handler-contributing registration.
    [Fact]
    public void AfterCall_ThePipelineIsSealed()
    {
        var builder = ServiceHostTestHost.CreateBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());

        Action act = () => builder.Services.AddMmcaApplicationPipeline();

        act.Should().Throw<InvalidOperationException>().WithMessage("*already closed*");
    }
}
