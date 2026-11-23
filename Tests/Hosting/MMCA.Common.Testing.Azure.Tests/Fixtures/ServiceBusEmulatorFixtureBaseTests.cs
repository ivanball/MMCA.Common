using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Testing.Azure.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Azure.Tests.Fixtures;

/// <summary>
/// Covers the container-free contract of <see cref="ServiceBusEmulatorFixtureBase"/>: the image pin, the
/// bounded-phase budgets, the admin-plane connection-string and host-configuration composition, the
/// no-host default, and what <c>RecordConsumed</c> registers. Starting the emulator needs a Docker
/// daemon (two containers and a warm-up measured in tens of seconds), which belongs in the
/// azure-integration tier (MMCA.Common.Azure.ServiceBusEmulator.Tests) and the consumers' broker-parity
/// tiers; everything asserted here would otherwise only be verified there.
/// </summary>
public class ServiceBusEmulatorFixtureBaseTests
{
    // The HTTP management plane shipped in emulator 2.0.0, and the transport provisions its whole
    // topology through it. A silent downgrade to a 1.x image would leave the broker unusable rather than
    // merely older.
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
    public void ComposeHostConfiguration_SelectsTheAzureServiceBusTransportOnTheEmulator()
    {
        var configuration = ServiceBusEmulatorFixtureBase.ComposeHostConfiguration(
            "Endpoint=sb://localhost:5672;UseDevelopmentEmulator=true;", "http://localhost:5300", "smoke");

        configuration["MessageBus:Provider"].Should().Be("AzureServiceBus");
        configuration["MessageBus:ConnectionString"].Should().Be("Endpoint=sb://localhost:5672;UseDevelopmentEmulator=true;");
        configuration["MessageBus:EmulatorAdminEndpoint"].Should().Be("http://localhost:5300");
        configuration["MessageBus:QueueName"].Should().Be("smoke");
        configuration["Application:Namespace"].Should().Be("smoke", "two tiers on one emulator must never share a topic");
        configuration["MessageBus:EnableInbox"].Should().Be("false", "the tier has no database; the transport is under test, not the inbox");
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
    public void ReceiveQueueName_IsNullByDefault_SoTheBaseHostsNoTransport()
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
    public void ClientsAndHost_ThrowBeforeTheFixtureStarts_RatherThanHandingOutNull()
    {
        var fixture = new ProbeFixture();

        FluentActions.Invoking(() => fixture.Client).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => fixture.ConnectionString).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => fixture.AdminEndpoint).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => fixture.Services).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task PublishAsync_BeforeTheHostStarts_Throws()
    {
        var fixture = new ProbeFixture();

        await FluentActions.Awaiting(() => fixture.PublishAsync(new ProbeEvent(), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Consumed_StartsEmpty() =>
        new ProbeFixture().Consumed.Should().BeEmpty();

    [Fact]
    public async Task RecordConsumed_SubscribesTheEvent_AndRecordsEveryDelivery()
    {
        var fixture = new ProbeFixture();
        var services = new ServiceCollection();
        var consumers = new RecordingConsumerBuilder();

        fixture.Record<ProbeEvent>(consumers, services);

        consumers.Registered.Should().Equal(typeof(ProbeEvent));
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IIntegrationEventHandler<ProbeEvent>>();
        var delivered = new ProbeEvent();
        await handler.HandleAsync(delivered, TestContext.Current.CancellationToken);
        fixture.Consumed.Should().ContainSingle().Which.Should().BeSameAs(delivered);
    }

    [Fact]
    public async Task DisposeAsync_OnAFixtureThatNeverStarted_IsANoOp()
    {
        // The timeout paths in InitializeAsync leave the later members unassigned, and xUnit still calls
        // DisposeAsync. Throwing here would mask the real phase failure.
        var fixture = new ProbeFixture();

        await FluentActions.Awaiting(async () => await fixture.DisposeAsync()).Should().NotThrowAsync();
    }

    public sealed record class ProbeEvent : BaseIntegrationEvent;

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

        public void Record<TEvent>(IBrokerConsumerBuilder consumers, IServiceCollection services)
            where TEvent : class, IIntegrationEvent =>
            RecordConsumed<TEvent>(consumers, services);
    }

    private sealed class OverridingFixture : ProbeFixture
    {
        protected override string EmulatorImage => "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0";

        protected override string? ReceiveQueueName => "smoke";

        protected override TimeSpan ContainerStartTimeout => TimeSpan.FromSeconds(30);

        protected override TimeSpan BusStartTimeout => TimeSpan.FromSeconds(20);

        protected override TimeSpan BusStopTimeout => TimeSpan.FromSeconds(10);
    }

    private sealed class RecordingConsumerBuilder : IBrokerConsumerBuilder
    {
        public List<Type> Registered { get; } = [];

        public IBrokerConsumerBuilder RegisterIntegrationEventConsumer<TEvent>()
            where TEvent : class, IIntegrationEvent
        {
            Registered.Add(typeof(TEvent));
            return this;
        }

        public IBrokerConsumerBuilder RegisterUpcastedIntegrationEventConsumer<TEvent>()
            where TEvent : class, IIntegrationEvent =>
            throw new InvalidOperationException("RecordConsumed registers the current contract only.");
    }
}
