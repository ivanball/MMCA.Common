using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class MessageBusSettingsTests
{
    [Fact]
    public void SectionName_IsMessageBus() =>
        MessageBusSettings.SectionName.Should().Be("MessageBus");

    // EnableInbox and EnableOutbox default to unset, so the transport decides (see the
    // IsInboxEnabled/IsOutboxEnabled theories below).
    // QueueName: unset derives the queue from the host application name.
    // ProvisionTopology: on by default, so a fresh environment creates its broker entities.
    // RedeliveryIntervalsSeconds: unset must stay null so the transport default applies. The binder
    // appends a configured list to a non-null initializer, so a default living on the property would
    // make the intervals impossible to replace by configuration.
    [Theory]
    [InlineData(nameof(MessageBusSettings.Provider), "InProcess")]
    [InlineData(nameof(MessageBusSettings.EnableInbox), null)]
    [InlineData(nameof(MessageBusSettings.EnableOutbox), null)]
    [InlineData(nameof(MessageBusSettings.QueueName), null)]
    [InlineData(nameof(MessageBusSettings.ProvisionTopology), true)]
    [InlineData(nameof(MessageBusSettings.RedeliveryIntervalsSeconds), null)]
    public void Default_PropertyHasTheDocumentedValue(string property, object? expected) =>
        typeof(MessageBusSettings).GetProperty(property)!.GetValue(new MessageBusSettings()).Should().Be(expected);

    // Unset resolves from the transport: a broker redelivers by contract, so dedup is ON; in-process
    // dispatch has no redelivery to dedup, so it stays OFF and the InboxMessages table is untouched.
    [Theory]
    [InlineData("InProcess", false)]
    [InlineData("RabbitMq", true)]
    [InlineData("AzureServiceBus", true)]
    [InlineData("inprocess", false)]
    [InlineData("", false)]
    public void IsInboxEnabled_Unset_ResolvesFromTheTransport(string provider, bool expected) =>
        new MessageBusSettings { Provider = provider }.IsInboxEnabled.Should().Be(expected);

    // An explicit value wins in BOTH directions: a host that has not migrated InboxMessages can turn
    // it off under a broker, and a monolith can turn it on.
    [Theory]
    [InlineData("RabbitMq", false)]
    [InlineData("AzureServiceBus", false)]
    [InlineData("InProcess", true)]
    public void IsInboxEnabled_Explicit_OverridesTheTransportDefault(string provider, bool configured) =>
        new MessageBusSettings { Provider = provider, EnableInbox = configured }
            .IsInboxEnabled.Should().Be(configured);

    // Unset resolves from the transport, same shape as the inbox: a broker deployment publishes
    // exclusively through the outbox, while an in-process host dispatches inside the process that
    // raised the event and pays nothing for store-and-forward it never uses.
    [Theory]
    [InlineData("InProcess", false)]
    [InlineData("RabbitMq", true)]
    [InlineData("AzureServiceBus", true)]
    public void IsOutboxEnabled_Unset_ResolvesFromTheTransport(string provider, bool expected) =>
        new MessageBusSettings { Provider = provider }.IsOutboxEnabled.Should().Be(expected);

    // A monolith that wants at-least-once delivery across a crash turns it back on. Turning it OFF
    // under a broker parses here and is refused at registration instead, where the failure can name
    // the consequence.
    [Theory]
    [InlineData("InProcess", true)]
    [InlineData("InProcess", false)]
    public void IsOutboxEnabled_Explicit_OverridesTheTransportDefault(string provider, bool configured) =>
        new MessageBusSettings { Provider = provider, EnableOutbox = configured }
            .IsOutboxEnabled.Should().Be(configured);

    [Fact]
    public void Default_RedeliveryIntervalsSeconds_IsOneMinuteTenMinutesOneHour() =>
        MessageBusSettings.DefaultRedeliveryIntervalsSeconds.Should().Equal(60, 600, 3600);

    [Fact]
    public void ResilienceProperties_RoundTrip()
    {
        var sut = new MessageBusSettings
        {
            EnableInbox = true,
            QueueName = "orders",
            ProvisionTopology = false,
            RedeliveryIntervalsSeconds = [5, 15],
        };

        sut.EnableInbox.Should().BeTrue();
        sut.QueueName.Should().Be("orders");
        sut.ProvisionTopology.Should().BeFalse();
        sut.RedeliveryIntervalsSeconds.Should().Equal(5, 15);
    }
}
