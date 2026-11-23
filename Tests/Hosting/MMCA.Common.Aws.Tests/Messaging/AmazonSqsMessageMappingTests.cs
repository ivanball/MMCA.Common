using System.Text;
using Amazon.SQS.Model;
using AwesomeAssertions;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Shared.Messaging;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// The wire mapping: every header packed into ONE message attribute and unpacked with its key case,
/// the body as UTF-8 JSON text, and the copies a redelivery and a dead letter send.
/// </summary>
public sealed class AmazonSqsMessageMappingTests
{
    /// <summary>A fully stamped message: more headers than the 10-attribute cap.</summary>
    private static Dictionary<string, string> FullyStampedHeaders() => new(StringComparer.Ordinal)
    {
        [BrokerHeaders.EventName] = "order-placed",
        [BrokerHeaders.MessageId] = "8f2c1d3e-0000-4000-8000-000000000001",
        [BrokerHeaders.ContentType] = BrokerHeaders.JsonContentType,
        [BrokerHeaders.RedeliveryCount] = "1",
        [BrokerHeaders.TraceParent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        [BrokerHeaders.TraceState] = "vendor=value",
        [MessageHeaders.TenantId] = "acme",
        [MessageHeaders.UserId] = "42",
        [MessageHeaders.UserRoles] = "Admin,Manager",
        [MessageHeaders.UserPermissions] = "[\"orders.read\",\"orders.write\"]",
        [MessageHeaders.CorrelationId] = "corr-1",
    };

    private static Message ReceivedWith(Dictionary<string, string> headers, string body = "{\"id\":1}") => new()
    {
        MessageId = "sqs-1",
        ReceiptHandle = "receipt-1",
        Body = body,
        MessageAttributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        {
            [AmazonSqsMessageMapping.HeadersAttribute] = new() { DataType = "String", StringValue = AmazonSqsMessageMapping.PackHeaders(headers) },
        },
    };

    [Fact]
    public void PackThenUnpack_RoundTripsEveryHeader_WithItsKeyCase()
    {
        var headers = FullyStampedHeaders();
        headers["Mixed-Case-Key"] = "quotes \" and \\ backslashes, unicode \u00e9 and <html>";

        var unpacked = AmazonSqsMessageMapping.UnpackHeaders(AmazonSqsMessageMapping.PackHeaders(headers));

        unpacked.Should().BeEquivalentTo(headers);
        unpacked.Keys.Should().Contain("Mixed-Case-Key").And.Contain(MessageHeaders.TenantId);
        unpacked.Comparer.Should().Be(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"an\",\"array\"]")]
    public void Unpack_WithoutAJsonObject_YieldsNoHeaders(string? packed) =>
        AmazonSqsMessageMapping.UnpackHeaders(packed).Should().BeEmpty();

    [Fact]
    public void Unpack_SkipsNonStringValues() =>
        AmazonSqsMessageMapping.UnpackHeaders("{\"a\":\"x\",\"b\":1,\"c\":null}").Should().ContainSingle().Which.Key.Should().Be("a");

    [Fact]
    public void ToPublishRequest_CarriesTheBodyAsText_AndEveryHeaderInOneAttribute()
    {
        var headers = FullyStampedHeaders();
        var outgoing = new OutgoingBrokerMessage("ns-order-placed", "order-placed", Guid.NewGuid(), Encoding.UTF8.GetBytes("{\"id\":1}"), headers);

        var request = AmazonSqsMessageMapping.ToPublishRequest("arn:aws:sns:us-east-1:000000000000:ns-order-placed", outgoing);

        request.TopicArn.Should().Be("arn:aws:sns:us-east-1:000000000000:ns-order-placed");
        request.Message.Should().Be("{\"id\":1}");
        headers.Count.Should().BeGreaterThan(10, "the reason the headers are packed");
        request.MessageAttributes.Should().ContainSingle().Which.Key.Should().Be(AmazonSqsMessageMapping.HeadersAttribute);
        var attribute = request.MessageAttributes[AmazonSqsMessageMapping.HeadersAttribute];
        attribute.DataType.Should().Be("String");
        AmazonSqsMessageMapping.UnpackHeaders(attribute.StringValue).Should().BeEquivalentTo(headers);
    }

    [Fact]
    public void ToIncoming_UnpacksTheHeaders_AndTakesTheBodyAndTheSqsMessageId()
    {
        var headers = FullyStampedHeaders();
        var message = ReceivedWith(headers);

        var incoming = AmazonSqsMessageMapping.ToIncoming(message, AmazonSqsMessageMapping.ToHeaders(message));

        incoming.Headers.Should().BeEquivalentTo(headers);
        incoming.EventName.Should().Be("order-placed");
        incoming.RedeliveryCount.Should().Be(1);
        Encoding.UTF8.GetString(incoming.Body.Span).Should().Be("{\"id\":1}");
        incoming.BrokerMessageId.Should().Be("sqs-1");
    }

    [Fact]
    public void ToHeaders_KeepsForeignStringAttributes_ButThePackedHeadersWin()
    {
        var message = ReceivedWith(new Dictionary<string, string>(StringComparer.Ordinal) { [BrokerHeaders.EventName] = "packed" });
        message.MessageAttributes["foreign"] = new MessageAttributeValue { DataType = "String", StringValue = "x" };
        message.MessageAttributes[BrokerHeaders.EventName] = new MessageAttributeValue { DataType = "String", StringValue = "loose" };
        message.MessageAttributes["binary"] = new MessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream([1]) };

        var headers = AmazonSqsMessageMapping.ToHeaders(message);

        headers.Should().HaveCount(2);
        headers["foreign"].Should().Be("x");
        headers[BrokerHeaders.EventName].Should().Be("packed");
    }

    [Fact]
    public void ToHeaders_WithNoAttributes_IsEmpty() =>
        AmazonSqsMessageMapping.ToHeaders(new Message { Body = "{}" }).Should().BeEmpty();

    [Fact]
    public void RedeliveryHeaders_SetTheCount_AndKeepEverythingElse()
    {
        var headers = FullyStampedHeaders();

        var copy = AmazonSqsMessageMapping.RedeliveryHeaders(headers, 3);

        copy[BrokerHeaders.RedeliveryCount].Should().Be("3");
        copy.Where(h => h.Key != BrokerHeaders.RedeliveryCount).Should().BeEquivalentTo(headers.Where(h => h.Key != BrokerHeaders.RedeliveryCount));
        headers[BrokerHeaders.RedeliveryCount].Should().Be("1", "the received headers are not mutated");
    }

    [Fact]
    public void DeadLetterHeaders_AddTheReasonAndTheTruncatedDetail()
    {
        var detail = new string('x', AmazonSqsMessageMapping.MaxDeadLetterTextLength + 50);

        var headers = AmazonSqsMessageMapping.DeadLetterHeaders(FullyStampedHeaders(), "handler-failed", detail);

        headers[AmazonSqsMessageMapping.DeadLetterReasonHeader].Should().Be("handler-failed");
        headers[AmazonSqsMessageMapping.DeadLetterDetailHeader].Should().HaveLength(AmazonSqsMessageMapping.MaxDeadLetterTextLength);
        headers[MessageHeaders.TenantId].Should().Be("acme");
    }

    [Fact]
    public void DeadLetterHeaders_WithNoDetail_CarryOnlyTheReason()
    {
        var received = FullyStampedHeaders();
        received[AmazonSqsMessageMapping.DeadLetterDetailHeader] = "stale";

        var headers = AmazonSqsMessageMapping.DeadLetterHeaders(received, "poison", null);

        headers[AmazonSqsMessageMapping.DeadLetterReasonHeader].Should().Be("poison");
        headers.Should().NotContainKey(AmazonSqsMessageMapping.DeadLetterDetailHeader);
    }

    [Fact]
    public void ToSendRequest_PacksTheHeaders_AndSetsTheDelay()
    {
        var headers = FullyStampedHeaders();

        var request = AmazonSqsMessageMapping.ToSendRequest("https://sqs/q", "{}", headers, 120);

        request.QueueUrl.Should().Be("https://sqs/q");
        request.MessageBody.Should().Be("{}");
        request.DelaySeconds.Should().Be(120);
        request.MessageAttributes.Should().ContainSingle();
        AmazonSqsMessageMapping.UnpackHeaders(request.MessageAttributes[AmazonSqsMessageMapping.HeadersAttribute].StringValue)
            .Should().BeEquivalentTo(headers);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.2, 1)]
    [InlineData(60, 60)]
    [InlineData(900, 900)]
    [InlineData(3600, 900)]
    [InlineData(-5, 0)]
    public void ToDelaySeconds_RoundsUp_AndClampsToTheSqsCap(double seconds, int expected) =>
        AmazonSqsMessageMapping.ToDelaySeconds(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
