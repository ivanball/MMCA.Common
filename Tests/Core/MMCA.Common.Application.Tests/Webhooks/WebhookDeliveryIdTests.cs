using AwesomeAssertions;
using MMCA.Common.Application.Webhooks.Delivery;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <see cref="WebhookDeliveryId"/> (rule 35.26): a deterministic id per (event, subscription), so a
/// redelivered event schedules the same <c>webhook-id</c> and a receiver can dedupe on it.
/// </summary>
public sealed class WebhookDeliveryIdTests
{
    private static readonly Guid EventId = new(0xb6f2c4e, 0x1d3a, 0x4a8b, 0x9c, 0x7e, 0x5f, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e) /* 0b6f2c4e-1d3a-4a8b-9c7e-5f1a2b3c4d5e */;
    private static readonly Guid SubscriptionId = new(0x9a8b7c6d, 0x5e4f, 0x4a3b, 0x8c, 0x2d, 0x1e, 0xf, 0x9a, 0x8b, 0x7c, 0x6d) /* 9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d */;
    private static readonly Guid OtherSubscriptionId = new(0x11111111, 0x2222, 0x4333, 0x84, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55) /* 11111111-2222-4333-8444-555555555555 */;

    [Fact]
    public void SameInputs_SameId() => WebhookDeliveryId.For(EventId, SubscriptionId).Should().Be(WebhookDeliveryId.For(EventId, SubscriptionId));

    [Fact]
    public void SameInputs_SameId_AcrossFreshGuidInstances()
    {
        var eventCopy = Guid.Parse(EventId.ToString());
        var subscriptionCopy = Guid.Parse(SubscriptionId.ToString());

        WebhookDeliveryId.For(eventCopy, subscriptionCopy).Should().Be(WebhookDeliveryId.For(EventId, SubscriptionId));
    }

    [Fact]
    public void DifferentSubscription_DifferentId() => WebhookDeliveryId.For(EventId, SubscriptionId).Should().NotBe(WebhookDeliveryId.For(EventId, OtherSubscriptionId));

    [Fact]
    public void DifferentEvent_DifferentId() => WebhookDeliveryId.For(EventId, SubscriptionId).Should().NotBe(WebhookDeliveryId.For(Guid.NewGuid(), SubscriptionId));

    [Fact]
    public void SwappedArguments_DifferentId() =>
        // A hash over the two GUIDs' bytes in order is not symmetric (an XOR would be).
        WebhookDeliveryId.For(EventId, SubscriptionId).Should().NotBe(WebhookDeliveryId.For(SubscriptionId, EventId));

    [Fact]
    public void Id_IsNeitherEmptyNorEitherInput()
    {
        var id = WebhookDeliveryId.For(EventId, SubscriptionId);

        id.Should().NotBe(Guid.Empty);
        id.Should().NotBe(EventId);
        id.Should().NotBe(SubscriptionId);
    }
}
