using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Startup;
using MMCA.Common.Application;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// Options for <c>AddMmcaServiceHost</c>: the values and callbacks that differ between service hosts.
/// The defaults are the REST + gRPC service profile (an Http2-only cleartext endpoint, forwarded JWT
/// validation, no Redis-backed output cache).
/// </summary>
public sealed class MmcaServiceHostOptions
{
    /// <summary>
    /// The rolling log file path passed to <c>AddCommonSerilog</c> (for example
    /// <c>logs/MMCAStoreCatalogService.txt</c>); the file sink is skipped in Production.
    /// </summary>
    public required string LogFilePath { get; init; }

    /// <summary>
    /// The assemblies module discovery looks in; at least one. A service host names only its own
    /// module assemblies, so peer modules are never loaded.
    /// </summary>
    public required IReadOnlyList<Assembly> ModuleAssemblies { get; init; }

    /// <summary>
    /// The application pipeline body (ADR-014), invoked once with the module host the call returns:
    /// typically <c>pipeline.Register(moduleHost.RegisterModules)</c> plus the host's gRPC clients and
    /// <c>AddBrokerMessaging</c>, in the order the host needs.
    /// </summary>
    public required Action<MmcaApplicationPipelineBuilder, ModuleHostContext> ConfigurePipeline { get; init; }

    /// <summary>
    /// The protocols of the default Kestrel endpoints (<c>ConfigureEndpointsWithHealthProbe</c>).
    /// <see cref="HttpProtocols.Http2"/> for an h2c service; <see cref="HttpProtocols.Http1AndHttp2"/> for
    /// the ADR-012 mixed profile.
    /// </summary>
    public HttpProtocols Protocols { get; init; } = HttpProtocols.Http2;

    /// <summary>
    /// Whether the cleartext endpoint is re-declared when the dedicated health-probe listener is on
    /// (<c>ConfigureEndpointsWithHealthProbe</c>); <see langword="false"/> for a host that declares its
    /// endpoints in the <c>Kestrel:Endpoints</c> configuration section.
    /// </summary>
    public bool RedeclareCleartextEndpoint { get; init; } = true;

    /// <summary>How the host validates bearer tokens.</summary>
    public ServiceHostAuthentication Authentication { get; init; } = ServiceHostAuthentication.ForwardedJwtBearer;

    /// <summary>Whether the output cache is stored in Redis (<c>AddRedisOutputCaching</c>).</summary>
    public bool UseRedisOutputCaching { get; init; }

    /// <summary>
    /// Adds the host's named output-cache policies. Runs after the always-present no-cache base policy.
    /// </summary>
    public Action<OutputCacheOptions>? ConfigureOutputCache { get; init; }

    /// <summary>
    /// Adds configuration sources (Key Vault, for example). Runs FIRST, before any configuration is read,
    /// so every eager bind below sees the values it adds.
    /// </summary>
    public Action<WebApplicationBuilder>? ConfigureConfigurationSources { get; init; }

    /// <summary>
    /// Runs immediately after <c>AddServiceDefaults()</c>: the place for vendor exporters that must follow
    /// it (the Azure Monitor exporter) and the shared DataProtection key ring.
    /// </summary>
    public Action<WebApplicationBuilder>? AfterServiceDefaults { get; init; }

    /// <summary>
    /// The host's own registrations that must precede the application pipeline (jobs, audit trail, blob
    /// storage, settings binds, extra authentication). Runs after <c>AddAPI</c>.
    /// </summary>
    public Action<WebApplicationBuilder>? ConfigureServices { get; init; }

    /// <summary>
    /// Registers broker transports (the Azure Service Bus transport, for example). Runs immediately before
    /// the application pipeline, because <c>AddBrokerMessaging</c> picks its transport while it registers.
    /// </summary>
    public Action<IServiceCollection>? RegisterBrokerTransports { get; init; }
}
