using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Application.Webhooks.Delivery;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <see cref="WebhookEnvelope"/> (rule 35.30): the Standard Webhooks payload shape
/// <c>{"type":...,"timestamp":...,"data":...}</c>, with the event payload carried verbatim.
/// </summary>
public sealed class WebhookEnvelopeTests
{
    private static readonly DateTime OccurredOn = new DateTime(2026, 11, 12, 9, 30, 15, DateTimeKind.Utc).AddTicks(1_234_567);

    [Fact]
    public void Build_EmitsTypeTimestampAndVerbatimData()
    {
        const string payload = """{"ticketId":42,"title":"Printer, again","tags":["a","b"]}""";

        var body = WebhookEnvelope.Build("ticket.opened", OccurredOn, payload);

        body.Should().Be(
            "{\"type\":\"ticket.opened\",\"timestamp\":\"" + OccurredOn.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\",\"data\":" + payload + "}");
    }

    [Fact]
    public void Build_IsValidJson_WithTheThreeMembers()
    {
        const string payload = """{"ticketId":42}""";

        using var document = JsonDocument.Parse(WebhookEnvelope.Build("ticket.opened", OccurredOn, payload));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Equal("type", "timestamp", "data");
        root.GetProperty("type").GetString().Should().Be("ticket.opened");
        root.GetProperty("timestamp").GetString().Should().Be("2026-11-12T09:30:15.1234567Z");
        root.GetProperty("timestamp").GetDateTime().Should().Be(OccurredOn);
        root.GetProperty("data").GetRawText().Should().Be(payload);
    }

    [Fact]
    public void Build_DoesNotReformatTheData()
    {
        // Whitespace and member order inside data are the payload's own; the envelope copies it.
        const string payload = """{ "z": 1,  "a": [ 1, 2 ] }""";

        WebhookEnvelope.Build("order.paid", OccurredOn, payload).Should().Contain("\"data\":" + payload);
    }
}
