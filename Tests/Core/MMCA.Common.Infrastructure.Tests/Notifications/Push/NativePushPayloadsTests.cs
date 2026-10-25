using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Infrastructure.Tests.Notifications.Push;

/// <summary>
/// Tests for <see cref="NativePushPayloads"/> (ADR-044): the FCM v1 / APNs payload shapes every native
/// push adapter hands to the platforms (Notification Hubs verbatim, SNS inside its per-platform
/// message structure). The two pin tests hold the exact bytes the builders produced while they were
/// internal to the Azure adapter, so moving them into the core changed nothing a device receives.
/// </summary>
public sealed class NativePushPayloadsTests
{
    private static Dictionary<string, string> PinnedMetadata() => new()
    {
        ["route"] = "/notifications/7",
        ["kind"] = "a&b <c> 'd' \"e\" caf\u00e9",
        ["aps"] = "x",
    };

    [Fact]
    public void BuildFcmV1Payload_WrapsNotificationInMessageEnvelope()
    {
        var json = NativePushPayloads.BuildFcmV1Payload("Hello", "World");

        using var doc = JsonDocument.Parse(json);
        var notification = doc.RootElement.GetProperty("message").GetProperty("notification");
        notification.GetProperty("title").GetString().Should().Be("Hello");
        notification.GetProperty("body").GetString().Should().Be("World");
        doc.RootElement.GetProperty("message").TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildFcmV1Payload_CarriesMetadataAsDataKeys()
    {
        var json = NativePushPayloads.BuildFcmV1Payload(
            "Hello", "World", new Dictionary<string, string> { ["route"] = "/notifications" });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("message").GetProperty("data").GetProperty("route")
            .GetString().Should().Be("/notifications");
    }

    [Fact]
    public void BuildApnsPayload_PutsAlertUnderApsAndMetadataTopLevel()
    {
        var json = NativePushPayloads.BuildApnsPayload(
            "Hello", "World", new Dictionary<string, string> { ["route"] = "/notifications" });

        using var doc = JsonDocument.Parse(json);
        var alert = doc.RootElement.GetProperty("aps").GetProperty("alert");
        alert.GetProperty("title").GetString().Should().Be("Hello");
        alert.GetProperty("body").GetString().Should().Be("World");
        doc.RootElement.GetProperty("route").GetString().Should().Be("/notifications");
    }

    [Fact]
    public void BuildApnsPayload_MetadataCannotClobberTheApsBlock()
    {
        var json = NativePushPayloads.BuildApnsPayload(
            "Hello", "World", new Dictionary<string, string> { ["aps"] = "malicious" });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title")
            .GetString().Should().Be("Hello");
    }

    [Fact]
    public void BuildFcmV1Payload_IsByteIdenticalToThePreMoveOutput()
    {
        NativePushPayloads.BuildFcmV1Payload("Hello", "World", PinnedMetadata()).Should().Be(
            """{"message":{"notification":{"title":"Hello","body":"World"},"data":{"route":"/notifications/7","kind":"a\u0026b \u003Cc\u003E \u0027d\u0027 \u0022e\u0022 caf\u00E9","aps":"x"}}}""");
        NativePushPayloads.BuildFcmV1Payload("Hello", "World").Should().Be(
            """{"message":{"notification":{"title":"Hello","body":"World"}}}""");
    }

    [Fact]
    public void BuildApnsPayload_IsByteIdenticalToThePreMoveOutput()
    {
        NativePushPayloads.BuildApnsPayload("Hello", "World", PinnedMetadata()).Should().Be(
            """{"aps":{"alert":{"title":"Hello","body":"World"}},"route":"/notifications/7","kind":"a\u0026b \u003Cc\u003E \u0027d\u0027 \u0022e\u0022 caf\u00E9"}""");
        NativePushPayloads.BuildApnsPayload("Hello", "World").Should().Be(
            """{"aps":{"alert":{"title":"Hello","body":"World"}}}""");
    }
}
