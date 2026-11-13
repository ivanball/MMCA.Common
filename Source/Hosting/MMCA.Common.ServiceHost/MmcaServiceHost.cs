using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// What <c>AddMmcaServiceHost</c> returns and <c>UseMmcaServiceHostAsync</c> takes: the module host the
/// registration built, plus the bootstrap logger factory the module loader logs through, which the use
/// call disposes when the application stops.
/// </summary>
public sealed class MmcaServiceHost
{
    internal MmcaServiceHost(ModuleHostContext moduleHost, ILoggerFactory bootstrapLoggerFactory)
    {
        ModuleHost = moduleHost;
        BootstrapLoggerFactory = bootstrapLoggerFactory;
    }

    /// <summary>
    /// The module host: bound settings, the module loader and the discovery step (the same instance the
    /// <c>ConfigurePipeline</c> callback received).
    /// </summary>
    public ModuleHostContext ModuleHost { get; }

    /// <summary>
    /// The bootstrap logger factory the module loader's logger came from. Kept alive for the life of the
    /// host and disposed on <c>ApplicationStopped</c>.
    /// </summary>
    internal ILoggerFactory BootstrapLoggerFactory { get; }
}
