using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Testing.Fixtures;

/// <summary>
/// Makes an in-process host of a cross-service tier see only its OWN modules, exactly as it would in a
/// one-service-per-process deployment. Call it from <c>ConfigureTestServices</c> of every host factory a
/// <see cref="CrossServiceFixtureBase"/> subclass boots.
/// <para>
/// <b>Why it is needed.</b> The default <see cref="IEntityConfigurationAssemblyProvider"/> scans
/// <see cref="AppDomain.GetAssemblies"/>, and with several hosts in one process that scan returns every
/// host's module assemblies. Each host's entity registry then claims its peers' data sources (the fixture
/// configures <c>DataSources__{Module}</c> for every module), so its outbox and internal-command processors
/// drain the PEER databases too, and a row whose handler lives only in the peer host is dead-lettered as
/// <c>handler_missing</c> by the wrong host.
/// </para>
/// <para>
/// <b>What it keeps.</b> The registered provider still runs; its result is narrowed to the assemblies named
/// in the host's own <c>.deps.json</c> (the file <c>WebApplicationFactory</c> already requires next to the
/// test assembly), which is the set the host would load in its own process. Assemblies the host registered
/// explicitly through <see cref="EntityConfigurationOptions.AdditionalAssemblies"/> are kept unfiltered,
/// because that registration is already per host.
/// </para>
/// </summary>
public static class CrossServiceHostIsolation
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Narrows this host's entity-configuration assemblies (and so every data source its registry,
        /// outbox, internal-command and cleanup processors enumerate) to the host named by
        /// <typeparamref name="TEntryPoint"/>.
        /// </summary>
        /// <typeparam name="TEntryPoint">A type from the host's entry assembly, normally its <c>Program</c>.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">The host assembly has no <c>.deps.json</c> beside it.</exception>
        public IServiceCollection IsolateCrossServiceHost<TEntryPoint>()
            where TEntryPoint : class
        {
            var hostAssemblyNames = ReadHostAssemblyNames(typeof(TEntryPoint).Assembly);

            var registered = services
                .Where(static d => d.ServiceType == typeof(IEntityConfigurationAssemblyProvider) && !d.IsKeyedService)
                .ToList();
            foreach (var descriptor in registered)
            {
                services.Remove(descriptor);
            }

            var inner = registered.Count == 0 ? null : registered[^1];
            services.AddSingleton<IEntityConfigurationAssemblyProvider>(sp => new HostScopedAssemblyProvider(
                CreateInner(sp, inner),
                hostAssemblyNames,
                sp.GetService<IOptions<EntityConfigurationOptions>>()?.Value.AdditionalAssemblies ?? []));

            return services;
        }
    }

    private static IEntityConfigurationAssemblyProvider CreateInner(IServiceProvider sp, ServiceDescriptor? descriptor) =>
        descriptor switch
        {
            null => ActivatorUtilities.CreateInstance<DefaultEntityConfigurationAssemblyProvider>(sp),
            { ImplementationInstance: IEntityConfigurationAssemblyProvider instance } => instance,
            { ImplementationFactory: { } factory } => (IEntityConfigurationAssemblyProvider)factory(sp),
            { ImplementationType: { } type } => (IEntityConfigurationAssemblyProvider)ActivatorUtilities.CreateInstance(sp, type),
            _ => throw new InvalidOperationException("The registered IEntityConfigurationAssemblyProvider could not be constructed."),
        };

    /// <summary>
    /// Reads the simple names of every runtime assembly the host's <c>.deps.json</c> lists, plus the host
    /// assembly itself.
    /// </summary>
    private static HashSet<string> ReadHostAssemblyNames(Assembly hostAssembly)
    {
        var hostName = hostAssembly.GetName().Name
            ?? throw new InvalidOperationException("The host assembly has no name.");
        var depsPath = string.IsNullOrEmpty(hostAssembly.Location)
            ? null
            : Path.ChangeExtension(hostAssembly.Location, ".deps.json");
        if (depsPath is null || !File.Exists(depsPath))
        {
            throw new InvalidOperationException(
                $"Cannot isolate host '{hostName}': its .deps.json was not found beside the assembly ('{depsPath}'). "
                + "WebApplicationFactory copies it for every referenced host project.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hostName };
        using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        if (!document.RootElement.TryGetProperty("targets", out var targets))
        {
            return names;
        }

        foreach (var library in targets.EnumerateObject().SelectMany(static target => target.Value.EnumerateObject()))
        {
            if (library.Value.ValueKind == JsonValueKind.Object
                && library.Value.TryGetProperty("runtime", out var runtime)
                && runtime.ValueKind == JsonValueKind.Object)
            {
                foreach (var file in runtime.EnumerateObject())
                {
                    names.Add(Path.GetFileNameWithoutExtension(file.Name));
                }
            }
        }

        return names;
    }

    private sealed class HostScopedAssemblyProvider(
        IEntityConfigurationAssemblyProvider inner,
        HashSet<string> hostAssemblyNames,
        IReadOnlyCollection<Assembly> explicitAssemblies) : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() =>
            [.. inner.GetConfigurationAssemblies()
                .Where(a => explicitAssemblies.Contains(a)
                    || a.GetName().Name is { } name && hostAssemblyNames.Contains(name))];
    }
}
