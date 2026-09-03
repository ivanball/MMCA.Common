using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Graceful-shutdown gate for a host (rubric section 29): a requested stop must drain and complete
/// cleanly, firing <see cref="IHostApplicationLifetime.ApplicationStopping"/> then
/// <see cref="IHostApplicationLifetime.ApplicationStopped"/>, well within a bounded timeout.
/// <para>
/// The failure this catches is a hosted service (warm-up runner, service discovery, proxy
/// infrastructure) that refuses to drain. In production that does not announce itself: it silently
/// wedges a rolling deploy while the platform waits out its termination grace period. Here the
/// bounded stop token cancels, <see cref="IHost.StopAsync"/> throws, and the test fails instead.
/// </para>
/// <para>
/// Subclass with the host's entry point and no body; override <see cref="CreateFactory"/> only if
/// the host needs a fixture beyond a Production-pinned boot.
/// </para>
/// </summary>
/// <typeparam name="TEntryPoint">The host's entry-point class, typically its <c>Program</c>.</typeparam>
public abstract class GracefulShutdownTestsBase<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>Seconds allowed for the graceful stop before the test fails it.</summary>
    protected virtual int ShutdownTimeoutSeconds => 20;

    /// <summary>Creates the factory used to boot the host under test.</summary>
    protected virtual ProductionHostApplicationFactory<TEntryPoint> CreateFactory() => new();

    [Fact]
    public async Task Host_StopsGracefully_FiringLifetimeEventsWithinTimeout()
    {
        var factory = CreateFactory();

        // Held as a separate ConfiguredAsyncDisposable rather than "await using var factory = ...
        // .ConfigureAwait(false)": that form would retype factory and lose CreateClient/StartedHost.
        await using var factoryDisposal = factory.ConfigureAwait(false);

        using (factory.CreateClient())
        {
            // Creating a client boots and starts the host; StartedHost is now set.
        }

        factory.StartedHost.Should().NotBeNull();
        var host = factory.StartedHost!;
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.IsCancellationRequested.Should().BeTrue("the host should have started");

        // A real graceful stop honoring a bounded timeout: if a hosted service refuses to drain, the
        // token cancels and StopAsync throws, surfacing the non-graceful shutdown as a test failure.
        // Reaching the assertions below means StopAsync returned cleanly within the timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
        await host.StopAsync(timeout.Token).ConfigureAwait(false);

        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeTrue(
            "ApplicationStopping must fire during a graceful shutdown");
        lifetime.ApplicationStopped.IsCancellationRequested.Should().BeTrue(
            "ApplicationStopped must signal once the host has fully stopped");
    }
}
