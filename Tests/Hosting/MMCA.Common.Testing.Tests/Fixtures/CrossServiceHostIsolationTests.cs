using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Tests.Fixtures;

/// <summary>
/// Covers <see cref="CrossServiceHostIsolation"/>: in a cross-service tier every host shares one
/// process, so the default AppDomain scan hands each host its PEER's module assemblies too, and each
/// host's outbox and internal-command processors then drain the peer's database. Isolation narrows the
/// scan to what the host would load in its own process (its <c>.deps.json</c>), keeping only explicit
/// per-host registrations on top.
/// </summary>
public sealed class CrossServiceHostIsolationTests
{
    private const string PeerName = "Peer.Module.Infrastructure";

    // A module assembly that belongs to ANOTHER host in the same process: loaded into the AppDomain,
    // absent from this host's deps.json. Created once; a non-collectible dynamic assembly lives for the
    // whole test process, which is exactly what a peer host's assembly does in the real tier.
    private static readonly Assembly PeerModuleInfrastructure = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName(PeerName),
        AssemblyBuilderAccess.Run);

    [Fact]
    public void DefaultScan_WithoutIsolation_HandsTheHostItsPeersModule()
    {
        using var provider = BuildDefaultScan(isolate: false);

        ScannedNames(provider).Should().Contain(PeerName, "this is the cross-host leak isolation exists to remove");
    }

    [Fact]
    public void IsolateCrossServiceHost_DropsAModuleOutsideTheHostsDependencies()
    {
        using var provider = BuildDefaultScan(isolate: true);

        ScannedNames(provider).Should().NotContain(PeerName, "a one-service-per-process host never loads its peer's module");
    }

    [Fact]
    public void IsolateCrossServiceHost_KeepsTheHostsOwnAssembliesFromTheRegisteredProvider()
    {
        var own = typeof(CrossServiceFixtureBase).Assembly;
        var services = new ServiceCollection();
        services.AddSingleton<IEntityConfigurationAssemblyProvider>(new FixedProvider(own, PeerModuleInfrastructure));
        services.IsolateCrossServiceHost<CrossServiceHostIsolationTests>();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEntityConfigurationAssemblyProvider>().GetConfigurationAssemblies()
            .Should().Equal([own], "the host's own dependency survives, its peer's does not, and order is kept");
    }

    [Fact]
    public void IsolateCrossServiceHost_KeepsAnAssemblyTheHostRegisteredExplicitly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEntityConfigurationAssemblyProvider>(new FixedProvider(PeerModuleInfrastructure));
        services.Configure<EntityConfigurationOptions>(o => o.AdditionalAssemblies.Add(PeerModuleInfrastructure));
        services.IsolateCrossServiceHost<CrossServiceHostIsolationTests>();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEntityConfigurationAssemblyProvider>().GetConfigurationAssemblies()
            .Should().Contain(PeerModuleInfrastructure, "an explicit registration is already per host, so it is never filtered");
    }

    // The AppDomain hands back the dynamic assembly's runtime view, not the builder instance, so the
    // scan is compared by simple name.
    private static string[] ScannedNames(ServiceProvider provider) =>
        [.. provider.GetRequiredService<IEntityConfigurationAssemblyProvider>().GetConfigurationAssemblies()
            .Select(static a => a.GetName().Name ?? string.Empty)];

    private static ServiceProvider BuildDefaultScan(bool isolate)
    {
        _ = PeerModuleInfrastructure.FullName;
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IEntityConfigurationAssemblyProvider, DefaultEntityConfigurationAssemblyProvider>();
        if (isolate)
        {
            services.IsolateCrossServiceHost<CrossServiceHostIsolationTests>();
        }

        return services.BuildServiceProvider();
    }

    private sealed class FixedProvider(params Assembly[] assemblies) : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => assemblies;
    }
}
