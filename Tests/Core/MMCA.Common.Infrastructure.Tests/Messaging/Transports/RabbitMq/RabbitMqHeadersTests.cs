using System.Text;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>Header encoding: values go out as strings and come back from the client's UTF-8 bytes, keys keep their case.</summary>
public sealed class RabbitMqHeadersTests
{
    [Fact]
    public void Encode_WritesEveryValueAsAString_KeepingKeyCase()
    {
        var table = RabbitMqHeaders.Encode(new Dictionary<string, string>
        {
            ["mmca-event-name"] = "order-placed",
            ["MMCA-UserId"] = "42",
        });

        table.Should().HaveCount(2);
        table["mmca-event-name"].Should().Be("order-placed");
        table["MMCA-UserId"].Should().BeOfType<string>().Which.Should().Be("42");
    }

    [Fact]
    public void Decode_ReadsUtf8Bytes_KeepingKeyCase()
    {
        var headers = RabbitMqHeaders.Decode(new Dictionary<string, object?>
        {
            ["mmca-event-name"] = Encoding.UTF8.GetBytes("order-placed"),
            ["MMCA-TenantId"] = Encoding.UTF8.GetBytes("ténant"),
            ["plain"] = "text",
        });

        headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["mmca-event-name"] = "order-placed",
            ["MMCA-TenantId"] = "ténant",
            ["plain"] = "text",
        });
        headers.ContainsKey("mmca-tenantid").Should().BeFalse("keys compare ordinally, as the pipeline looks them up");
    }

    [Fact]
    public void Decode_FormatsScalarsInvariantly_AndDropsBrokerAndStructuredHeaders()
    {
        var headers = RabbitMqHeaders.Decode(new Dictionary<string, object?>
        {
            ["count"] = 3,
            ["flag"] = true,
            ["ratio"] = 1.5d,
            ["x-death"] = new List<object> { new Dictionary<string, object?>() },
            ["x-first-death-queue"] = Encoding.UTF8.GetBytes("demo-billing-delay-60s"),
            ["nested"] = new Dictionary<string, object?>(),
            ["nothing"] = null,
        });

        headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["count"] = "3",
            ["flag"] = "true",
            ["ratio"] = "1.5",
        });
    }

    [Fact]
    public void Decode_NoTable_IsEmpty() => RabbitMqHeaders.Decode(null).Should().BeEmpty();

    [Fact]
    public void RoundTrip_ThroughBytes_ReturnsThePublishedHeaders()
    {
        var published = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["MMCA-Permissions"] = "orders.read,orders.write",
        };

        var onTheWire = RabbitMqHeaders.Encode(published)
            .ToDictionary(pair => pair.Key, pair => (object?)Encoding.UTF8.GetBytes((string)pair.Value!), StringComparer.Ordinal);

        RabbitMqHeaders.Decode(onTheWire).Should().BeEquivalentTo(published);
    }
}
