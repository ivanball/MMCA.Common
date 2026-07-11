using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="NativePushPayloads"/> (ADR-044): the FCM v1 / APNs payload shapes the
/// hub forwards verbatim to the platforms, and the 20-tag chunking rule imposed by the Azure
/// Notification Hubs tag-expression cap.
/// </summary>
public sealed class NativePushPayloadsTests
{
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
    public void BuildUserTagExpressions_JoinsTagsWithOr()
    {
        var expressions = NativePushPayloads.BuildUserTagExpressions([1, 2, 3]).ToList();

        expressions.Should().ContainSingle()
            .Which.Should().Be("user:1 || user:2 || user:3");
    }

    [Fact]
    public void BuildUserTagExpressions_ChunksAtTheHubTagCap()
    {
        var userIds = Enumerable.Range(1, NativePushPayloads.MaxTagsPerExpression * 2 + 5);

        var expressions = NativePushPayloads.BuildUserTagExpressions(userIds).ToList();

        expressions.Should().HaveCount(3);
        expressions[0].Split(" || ").Should().HaveCount(NativePushPayloads.MaxTagsPerExpression);
        expressions[2].Split(" || ").Should().HaveCount(5);
    }

    [Fact]
    public void BuildUserTagExpressions_WithNoUsers_YieldsNothing()
        => NativePushPayloads.BuildUserTagExpressions([]).Should().BeEmpty();
}
