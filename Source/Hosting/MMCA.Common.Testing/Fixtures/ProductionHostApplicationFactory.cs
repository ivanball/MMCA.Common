using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Testing.Conformance;

namespace MMCA.Common.Testing.Fixtures;

/// <summary>
/// Boots a real host in-process with the environment pinned to <c>Production</c>, and captures the
/// started <see cref="IHost"/> so a test can drive its lifetime directly.
/// <para>
/// Both parts matter. Pinning <c>Production</c> exercises the realistic branches that a default
/// <c>Development</c> boot skips (the restrictive CORS policy, HSTS emission, production-only
/// middleware), which is where host misconfiguration actually hides. Capturing the host is what
/// makes <see cref="GracefulShutdownTestsBase{TEntryPoint}"/> possible: <see cref="IHost.StopAsync"/>
/// cannot be reached through the <see cref="WebApplicationFactory{TEntryPoint}"/> surface alone.
/// </para>
/// <para>
/// Suited to hosts that need no database or broker to boot (a reverse-proxy gateway is the usual
/// case). A host that migrates or seeds on startup needs its own fixture.
/// </para>
/// </summary>
/// <typeparam name="TEntryPoint">The host's entry-point class, typically its <c>Program</c>.</typeparam>
public class ProductionHostApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>
    /// The started host. Null until the first client is created, because
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> builds the host lazily.
    /// </summary>
    public IHost? StartedHost { get; private set; }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");
        StartedHost = base.CreateHost(builder);
        return StartedHost;
    }
}
