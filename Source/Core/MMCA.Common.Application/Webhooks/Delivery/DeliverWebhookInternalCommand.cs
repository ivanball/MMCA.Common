using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// One webhook delivery: a durable internal-command row scheduled by the fan-out for each matching
/// subscription. The row is the delivery history (attempts, last error, dead letter, requeue); its
/// stable name never changes.
/// </summary>
/// <param name="SubscriptionId">The subscription to deliver to.</param>
/// <param name="DeliveryId">The deterministic <c>webhook-id</c> (<see cref="WebhookDeliveryId.For"/>).</param>
/// <param name="EventType">The webhook event type name.</param>
/// <param name="EventId">The integration event's <c>MessageId</c>.</param>
/// <param name="OccurredOn">The integration event's <c>DateOccurred</c>.</param>
/// <param name="PayloadJson">The event serialized by its runtime type with web defaults.</param>
[InternalCommandName("MMCA.Webhooks.Deliver.v1")]
public sealed record DeliverWebhookInternalCommand(
    Guid SubscriptionId,
    Guid DeliveryId,
    string EventType,
    Guid EventId,
    DateTime OccurredOn,
    string PayloadJson) : IInternalCommand;
