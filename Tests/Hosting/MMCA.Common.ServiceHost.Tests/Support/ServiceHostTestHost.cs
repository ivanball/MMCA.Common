using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;
using MMCA.Common.Application;

namespace MMCA.Common.ServiceHost.Tests.Support;

/// <summary>
/// Builds the <see cref="WebApplicationBuilder"/> and <see cref="MmcaServiceHostOptions"/> every
/// ServiceHost test starts from (design 19(c)): in-memory configuration carrying a database
/// initialization strategy, one SQL Server data source, the JWT audience and the JWT authority, plus
/// the <see cref="ServiceHostTestModule"/> switch. Nothing here opens a connection: the SQL Server
/// string points at a closed port and is only ever registered, never dialled.
/// </summary>
internal static class ServiceHostTestHost
{
    /// <summary>The configured forwarded-JWT authority (HTTPS, so metadata validation never objects).</summary>
    public const string Authority = "https://identity.servicehost.test";

    /// <summary>The configured JWT audience.</summary>
    public const string Audience = "mmca-servicehost-tests";

    /// <summary>A SQL Server connection string that is registered but never opened.</summary>
    public const string SqlServerConnectionString =
        "Server=127.0.0.1,1;Database=ServiceHostTests;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=True;Connect Timeout=1";

    /// <summary>A Redis connection string for the tests that need Redis configured (never dialled).</summary>
    public const string RedisConnectionString = "127.0.0.1:1,abortConnect=false,connectTimeout=100";

    /// <summary>The switch build-time OpenAPI generation sets; honoured only in Development.</summary>
    public const string DesignTimeKey = "MmcaOpenApiDesignTime";

    /// <summary>
    /// The value <c>DesignTimeDatabaseInitializationTests</c> uses: not a valid strategy, so a run
    /// that reaches <c>InitializeDatabaseAsync</c> throws at once, before any database is opened.
    /// </summary>
    public const string InvalidInitStrategy = "NotAStrategy";

    /// <summary>The module assemblies handed to <c>AddMmcaServiceHost</c>: the test assembly alone.</summary>
    public static IReadOnlyList<Assembly> ModuleAssemblies { get; } = [typeof(ServiceHostTestModule).Assembly];

    /// <summary>The configuration every test seeds before calling <c>AddMmcaServiceHost</c>.</summary>
    /// <returns>A fresh, mutable copy.</returns>
    public static Dictionary<string, string?> BaseSettings() => new(StringComparer.Ordinal)
    {
        ["ApplicationSettings:DatabaseInitStrategy"] = InvalidInitStrategy,
        ["DataSources:Test:SQLServerConnectionString"] = SqlServerConnectionString,
        ["Jwt:Audience"] = Audience,
        ["Authentication:JwtBearer:Authority"] = Authority,
        [$"Modules:{ServiceHostTestModule.ModuleName}:Enabled"] = "true",
    };

    /// <summary>
    /// Creates a builder seeded with <see cref="BaseSettings"/>, adjusted by
    /// <paramref name="adjustSettings"/> (remove a key to make it absent).
    /// </summary>
    /// <param name="environment">The host environment name.</param>
    /// <param name="adjustSettings">Optional edit of the seeded settings before they are added.</param>
    /// <returns>The builder, with no logging provider of its own.</returns>
    public static WebApplicationBuilder CreateBuilder(
        string? environment = null,
        Action<Dictionary<string, string?>>? adjustSettings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment ?? Environments.Development,
        });

        builder.Logging.ClearProviders();

        var settings = BaseSettings();
        adjustSettings?.Invoke(settings);
        builder.Configuration.AddInMemoryCollection(settings);

        return builder;
    }

    /// <summary>
    /// Creates a builder for the <c>UseMmcaServiceHostAsync</c> tests: Development, the design-time
    /// switch on (so initialization is skipped, design 19(c)), and an in-memory TestServer.
    /// <para>
    /// The default provider's ValidateOnBuild is turned off: these tests pin what the use call maps
    /// and when it disposes the bootstrap factory, not the completeness of a host's service graph,
    /// which depends on the host's own modules and broker wiring. The per-service
    /// <c>WebApplicationFactory&lt;Program&gt;</c> suites in ADC and Store own that check (CI only).
    /// Scope validation stays as Development sets it.
    /// </para>
    /// </summary>
    /// <param name="designTime">The value of the design-time switch, or <see langword="null"/> to leave it unset.</param>
    /// <returns>The builder.</returns>
    public static WebApplicationBuilder CreateAppBuilder(string? designTime = "true")
    {
        var builder = CreateBuilder(
            Environments.Development,
            settings =>
            {
                if (designTime is not null)
                {
                    settings[DesignTimeKey] = designTime;
                }
            });

        builder.WebHost.UseTestServer();

        // DESIGN-QUESTION: 19(c) does not say whether the use-call tests build with Development's
        // ValidateOnBuild. This harness turns it off (see the summary); if the implementer finds the
        // composed graph validates cleanly with no host modules, delete this call and keep the default.
        builder.Host.UseDefaultServiceProvider((context, options) =>
        {
            options.ValidateScopes = context.HostingEnvironment.IsDevelopment();
            options.ValidateOnBuild = false;
        });

        return builder;
    }

    /// <summary>A unique rolling-file path under the temp directory, so parallel tests never share a file.</summary>
    /// <returns>The path.</returns>
    public static string LogFilePath() =>
        Path.Combine(Path.GetTempPath(), "mmca-servicehost-tests", $"{Guid.NewGuid():N}.txt");

    /// <summary>The pipeline body a module host passes: discovery as a pipeline step.</summary>
    /// <param name="pipeline">The pipeline builder.</param>
    /// <param name="moduleHost">The module host context.</param>
    public static void RegisterModules(MmcaApplicationPipelineBuilder pipeline, ModuleHostContext moduleHost) =>
        pipeline.Register(moduleHost.RegisterModules);

    /// <summary>
    /// Options over the seeded defaults: the test assembly as the module set, discovery as the
    /// pipeline body, and whichever callbacks the test passes.
    /// </summary>
    /// <returns>The options.</returns>
    public static MmcaServiceHostOptions Options(
        Action<WebApplicationBuilder>? configureConfigurationSources = null,
        Action<WebApplicationBuilder>? afterServiceDefaults = null,
        Action<WebApplicationBuilder>? configureServices = null,
        Action<IServiceCollection>? registerBrokerTransports = null,
        Action<MmcaApplicationPipelineBuilder, ModuleHostContext>? configurePipeline = null,
        Action<OutputCacheOptions>? configureOutputCache = null,
        ServiceHostAuthentication authentication = ServiceHostAuthentication.ForwardedJwtBearer,
        bool useRedisOutputCaching = false,
        IReadOnlyList<Assembly>? moduleAssemblies = null) => new()
        {
            LogFilePath = LogFilePath(),
            ModuleAssemblies = moduleAssemblies ?? ModuleAssemblies,
            ConfigurePipeline = configurePipeline ?? RegisterModules,
            Authentication = authentication,
            UseRedisOutputCaching = useRedisOutputCaching,
            ConfigureOutputCache = configureOutputCache,
            ConfigureConfigurationSources = configureConfigurationSources,
            AfterServiceDefaults = afterServiceDefaults,
            ConfigureServices = configureServices,
            RegisterBrokerTransports = registerBrokerTransports,
        };
}
