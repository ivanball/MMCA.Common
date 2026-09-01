using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Covers the container-free contract of <see cref="ServiceBusEmulatorFixtureBase"/>: the image pin, the
/// bounded-phase budgets, the admin-plane connection-string composition, the no-bus default, and the
/// process-global MassTransit entity defaults the emulator's TTL quota forces. Starting the emulator needs
/// a Docker daemon (two containers and a warm-up measured in tens of seconds), which belongs in the
/// consumers' nightly broker-parity tiers; everything asserted here would otherwise only be verified
/// there.
/// </summary>
public class ServiceBusEmulatorFixtureBaseTests
{
    // The HTTP management plane shipped in emulator 2.0.0, and MassTransit provisions its whole topology
    // through it at bus start. A silent downgrade to a 1.x image would leave the broker unusable rather
    // than merely older.
    [Fact]
    public void DefaultEmulatorImage_IsPinnedToATwoPointXBuild() =>
        ServiceBusEmulatorFixtureBase.DefaultEmulatorImage
            .Should().StartWith("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.");

    [Fact]
    public void ComposeAdminConnectionString_TargetsTheMappedAdminPortInEmulatorForm()
    {
        // The container module's own connection string targets the mapped AMQP port, so the admin plane
        // needs its own. UseDevelopmentEmulator is not decoration: it keeps the Azure SDK clients on
        // plain TCP/HTTP against a local host.
        var composed = ServiceBusEmulatorFixtureBase.ComposeAdminConnectionString("127.0.0.1", 54321);

        composed.Should().Be(
            "Endpoint=sb://127.0.0.1:54321;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
    }

    [Fact]
    public void PhaseBudgets_AreBoundedByDefault()
    {
        // A step killed by the JOB timeout has its output discarded, so an unbounded phase leaves no
        // evidence of which phase hung. These defaults are what make a hang a named TimeoutException.
        var fixture = new ProbeFixture();

        fixture.Budgets.Should().Be((TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ReceiveQueueName_IsNullByDefault_SoTheBaseHostsNoBus()
    {
        var fixture = new ProbeFixture();

        fixture.QueueName.Should().BeNull();
    }

    [Fact]
    public void Overrides_AreHonoured()
    {
        var fixture = new OverridingFixture();

        fixture.Image.Should().Be("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0");
        fixture.QueueName.Should().Be("smoke");
        fixture.Budgets.Should().Be((TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Clients_ThrowBeforeTheFixtureStarts_RatherThanHandingOutNull()
    {
        var fixture = new ProbeFixture();

        FluentActions.Invoking(() => fixture.Client).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => fixture.BusControl).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Consumed_StartsEmpty()
    {
        var fixture = new ProbeFixture();

        fixture.Consumed.Should().BeEmpty();
        fixture.HostAddress.Should().Be(new Uri("sb://localhost/"));
    }

    [Fact]
    public void EntityDefaults_AreLoweredBeneathTheEmulatorsOneHourQuota()
    {
        // MassTransit v8 defaults (366d TTL, 427d auto-delete) are rejected outright by the emulator, so
        // the base lowers these process-global statics. Constructing any subclass is what triggers it.
        _ = new ProbeFixture();

        MassTransit.AzureServiceBusTransport.Defaults.DefaultMessageTimeToLive.Should().Be(TimeSpan.FromHours(1));
        MassTransit.AzureServiceBusTransport.Defaults.BasicMessageTimeToLive.Should().Be(TimeSpan.FromHours(1));
        MassTransit.AzureServiceBusTransport.Defaults.AutoDeleteOnIdle.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task DisposeAsync_OnAFixtureThatNeverStarted_IsANoOp()
    {
        // The timeout paths in InitializeAsync leave the later members unassigned, and xUnit still calls
        // DisposeAsync. Throwing here would mask the real phase failure.
        var fixture = new ProbeFixture();

        await FluentActions.Awaiting(async () => await fixture.DisposeAsync()).Should().NotThrowAsync();
    }

    /// <summary>
    /// A subclass that never starts a container, so the pins, budgets and hooks can be read on a machine
    /// with no Docker daemon. That this compiles and constructs is itself the assertion that the base
    /// builds its container inside <c>InitializeAsync</c> rather than in a field initializer.
    /// </summary>
    private class ProbeFixture : ServiceBusEmulatorFixtureBase
    {
        public string Image => EmulatorImage;

        public string? QueueName => ReceiveQueueName;

        public (TimeSpan Container, TimeSpan BusStart, TimeSpan BusStop) Budgets =>
            (ContainerStartTimeout, BusStartTimeout, BusStopTimeout);
    }

    private sealed class OverridingFixture : ProbeFixture
    {
        protected override string EmulatorImage => "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0";

        protected override string? ReceiveQueueName => "smoke";

        protected override TimeSpan ContainerStartTimeout => TimeSpan.FromSeconds(30);

        protected override TimeSpan BusStartTimeout => TimeSpan.FromSeconds(20);

        protected override TimeSpan BusStopTimeout => TimeSpan.FromSeconds(10);
    }
}
