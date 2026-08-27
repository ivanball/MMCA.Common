using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Application;

/// <summary>
/// Collects the handler registrations that must happen between <c>AddApplication()</c> and
/// <c>AddApplicationDecorators()</c>. Handed to the callback of
/// <c>AddMmcaApplicationPipeline(...)</c>; not constructible on its own, because outside that call
/// there is nothing keeping the decorators last.
/// </summary>
public sealed class MmcaApplicationPipelineBuilder
{
    internal MmcaApplicationPipelineBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// The service collection under construction, for registrations that need it directly.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Scans one module's Application assembly, identified by a marker type in it, exactly as
    /// <c>ScanModuleApplicationServices&lt;TAssemblyMarker&gt;()</c> does.
    /// </summary>
    /// <typeparam name="TAssemblyMarker">A type in the module's Application assembly (typically <c>ClassReference</c>).</typeparam>
    /// <returns>This builder, for chaining.</returns>
    public MmcaApplicationPipelineBuilder ScanModule<TAssemblyMarker>()
        where TAssemblyMarker : class
    {
        Services.ScanModuleApplicationServices<TAssemblyMarker>();
        return this;
    }

    /// <summary>
    /// Scans the given module Application assemblies, for hosts that resolve their module set at
    /// runtime rather than naming a marker type per module.
    /// </summary>
    /// <param name="moduleAssemblies">The module Application assemblies to scan.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleAssemblies"/> is <see langword="null"/>.</exception>
    public MmcaApplicationPipelineBuilder ScanModules(params Assembly[] moduleAssemblies)
    {
        ArgumentNullException.ThrowIfNull(moduleAssemblies);

        foreach (var moduleAssembly in moduleAssemblies)
        {
            Services.ScanModuleApplicationServices(moduleAssembly);
        }

        return this;
    }

    /// <summary>
    /// Runs an arbitrary registration step inside the pipeline, before the decorators close it. This
    /// is where a <c>ModuleLoader.DiscoverAndRegister(...)</c> call goes, along with anything else a
    /// host registers that ends up contributing or replacing a command/query handler: cross-service
    /// gRPC clients, broker messaging, per-host handler overrides.
    /// </summary>
    /// <param name="register">The registration step.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="register"/> is <see langword="null"/>.</exception>
    public MmcaApplicationPipelineBuilder Register(Action<IServiceCollection> register)
    {
        ArgumentNullException.ThrowIfNull(register);

        register(Services);
        return this;
    }
}
