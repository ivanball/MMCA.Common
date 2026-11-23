using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The wire mapping of the Azure Service Bus transport: what a publish sends, how a received message's
/// application properties become the pipeline's headers (key case preserved, values as strings), the
/// publish-to-receive round trip, and the copy a scheduled redelivery carries.
/// </summary>
public sealed class AzureServiceBusMessageMappingTests
{
    private static readonly Guid MessageId = new(0x6f1c2c4e, 0x8f53, 0x4f39, 0x9d, 0x1a, 0x0b, 0x5d, 0x7c, 0x1e, 0x2a, 0x33);

    private static OutgoingBrokerMessage Outgoing() =>
        new(
            "demo-order-placed",
            "order-placed",
            MessageId,
            Encoding.UTF8.GetBytes("{\"orderId\":42}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BrokerHeaders.EventName] = "order-placed",
                [BrokerHeaders.MessageId] = MessageId.ToString("D"),
                [BrokerHeaders.ContentType] = BrokerHeaders.JsonContentType,
                [BrokerHeaders.TraceParent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
                ["MMCA-UserId"] = "7",
            });

    [Fact]
    public void ToServiceBusMessage_CarriesTheBodyTheIdTheContentTypeAndEveryHeaderAsAString()
    {
        var outgoing = Outgoing();

        var message = AzureServiceBusMessageMapping.ToServiceBusMessage(outgoing);

        message.Body.ToArray().Should().Equal(outgoing.Body.ToArray());
        message.MessageId.Should().Be(MessageId.ToString("D"));
        message.ContentType.Should().Be("application/json");
        message.Subject.Should().Be("order-placed");
        message.ApplicationProperties.Should().HaveCount(outgoing.Headers.Count);
        foreach (var (key, value) in outgoing.Headers)
        {
            message.ApplicationProperties[key].Should().BeOfType<string>().Which.Should().Be(value);
        }
    }

    [Fact]
    public void PublishThenReceive_HandsThePipelineTheHeadersExactlyAsPublished()
    {
        var outgoing = Outgoing();
        var sent = AzureServiceBusMessageMapping.ToServiceBusMessage(outgoing);

        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            contentType: sent.ContentType,
            properties: new Dictionary<string, object>(sent.ApplicationProperties));
        var incoming = AzureServiceBusMessageMapping.ToIncoming(received);

        incoming.Body.ToArray().Should().Equal(outgoing.Body.ToArray());
        incoming.Headers.Should().BeEquivalentTo(outgoing.Headers);
        incoming.EventName.Should().Be("order-placed");
        incoming.RedeliveryCount.Should().Be(0);
        incoming.BrokerMessageId.Should().Be(MessageId.ToString("D"));
    }

    [Fact]
    public void ToHeaders_PreservesKeyCase_FormatsOtherTypesInvariantly_AndDropsNulls()
    {
        var properties = new Dictionary<string, object>
        {
            ["MMCA-TenantId"] = "acme",
            ["mmca-tenantid"] = "lower",
            ["count"] = 3,
            ["ratio"] = 1.5,
            ["when"] = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc),
            ["missing"] = null!,
        };

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var headers = AzureServiceBusMessageMapping.ToHeaders(properties);

            headers["MMCA-TenantId"].Should().Be("acme");
            headers["mmca-tenantid"].Should().Be("lower", "keys are compared ordinally, so case-distinct keys both survive");
            headers["count"].Should().Be("3");
            headers["ratio"].Should().Be("1.5", "the invariant culture formats the number, not the thread's");
            headers["when"].Should().Be(Convert.ToString(properties["when"], CultureInfo.InvariantCulture));
            headers.Should().NotContainKey("missing");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CreateRedelivery_CopiesEverything_AndSetsTheRedeliveryCount()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{\"orderId\":42}"),
            messageId: "m-1",
            contentType: "application/json",
            subject: "order-placed",
            properties: new Dictionary<string, object>
            {
                [BrokerHeaders.EventName] = "order-placed",
                [BrokerHeaders.RedeliveryCount] = "1",
                ["MMCA-UserId"] = "7",
            });

        var copy = AzureServiceBusMessageMapping.CreateRedelivery(received, redeliveryCount: 2);

        copy.Body.ToString().Should().Be("{\"orderId\":42}");
        copy.MessageId.Should().Be("m-1");
        copy.ContentType.Should().Be("application/json");
        copy.Subject.Should().Be("order-placed");
        copy.ApplicationProperties[BrokerHeaders.EventName].Should().Be("order-placed");
        copy.ApplicationProperties["MMCA-UserId"].Should().Be("7");
        copy.ApplicationProperties[BrokerHeaders.RedeliveryCount].Should().Be("2");
    }
}
