using AwesomeAssertions;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The Service Bus entity names and the options each entity is created with: names fitted to the
/// service's limits (260 for a queue or topic path, 50 for a subscription), the queue's dead-letter
/// posture, the forwarding subscription, and the emulator's one-hour ceiling applied only there.
/// </summary>
public sealed class AzureServiceBusTopologyTests
{
    private const string AdministrationConnectionString =
        "Endpoint=sb://mmca-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cmVkYWN0ZWQ=";

    [Fact]
    public void Names_WithinTheLimits_AreTheFrameworkNamesUnchanged()
    {
        AzureServiceBusEntityNames.Queue("mmca-adc-engagement").Should().Be("mmca-adc-engagement");
        AzureServiceBusEntityNames.Topic("adc-mmca-adc-identity-user-registered").Should().Be("adc-mmca-adc-identity-user-registered");
        AzureServiceBusEntityNames.Subscription("mmca-adc-engagement").Should().Be("mmca-adc-engagement");
    }

    [Fact]
    public void ALongQueueName_FitsTheSubscriptionLimit_Deterministically()
    {
        var queue = new string('q', 49) + "-engagement";

        var subscription = AzureServiceBusEntityNames.Subscription(queue);

        subscription.Should().HaveLength(50).And.Be(BrokerNaming.Fit(queue, 50));
        AzureServiceBusEntityNames.Subscription(queue).Should().Be(subscription, "every host computes the same name");
        AzureServiceBusEntityNames.Queue(queue).Should().Be(queue, "a queue path allows 260 characters");
    }

    [Fact]
    public void ALongTopic_FitsThePathLimit()
    {
        var topic = "demo-" + new string('e', 300);

        var fitted = AzureServiceBusEntityNames.Topic(topic);

        fitted.Should().HaveLength(260).And.Be(BrokerNaming.Fit(topic, 260));
    }

    [Fact]
    public void TheQueue_DeadLettersExpiredMessages_AndHasNoDuplicateDetection()
    {
        var options = AzureServiceBusTopologyProvisioner.BuildQueueOptions("orders", isEmulator: false);

        options.Name.Should().Be("orders");
        options.DeadLetteringOnMessageExpiration.Should().BeTrue();
        options.RequiresDuplicateDetection.Should().BeFalse("a scheduled redelivery reuses the original message id");
        options.DefaultMessageTimeToLive.Should().Be(new CreateQueueOptions("defaults").DefaultMessageTimeToLive);
    }

    [Fact]
    public void TheSubscription_IsNamedAfterTheQueue_AndForwardsToIt()
    {
        var options = AzureServiceBusTopologyProvisioner.BuildSubscriptionOptions("demo-order-placed", "orders", "orders", isEmulator: false);

        options.TopicName.Should().Be("demo-order-placed");
        options.SubscriptionName.Should().Be("orders");
        options.ForwardTo.Should().Be("orders");
    }

    [Fact]
    public void OnTheEmulator_EveryEntityStaysBeneathItsOneHourQuota()
    {
        AzureServiceBusTopologyProvisioner.BuildQueueOptions("orders", isEmulator: true).DefaultMessageTimeToLive.Should().Be(TimeSpan.FromHours(1));
        AzureServiceBusTopologyProvisioner.BuildTopicOptions("demo-order-placed", isEmulator: true).DefaultMessageTimeToLive.Should().Be(TimeSpan.FromHours(1));
        AzureServiceBusTopologyProvisioner.BuildSubscriptionOptions("demo-order-placed", "orders", "orders", isEmulator: true)
            .DefaultMessageTimeToLive.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void OnARealNamespace_TopicsKeepTheServiceDefaults() =>
        AzureServiceBusTopologyProvisioner.BuildTopicOptions("demo-order-placed", isEmulator: false)
            .DefaultMessageTimeToLive.Should().Be(new CreateTopicOptions("defaults").DefaultMessageTimeToLive);

    [Fact]
    public async Task ATopologyWithNoSubscriptions_ProvisionsNothing()
    {
        // The administration client points at a namespace that does not exist; any call would fail.
        var provisioner = new AzureServiceBusTopologyProvisioner(
            new ServiceBusAdministrationClient(AdministrationConnectionString), isEmulator: false, NullLogger.Instance);

        var act = () => provisioner.ProvisionAsync(new BrokerTopology("demo", "orders", []), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
