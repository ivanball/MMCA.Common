namespace MMCA.Common.Application.Webhooks;

/// <summary>One integration event type published as a webhook, registered by <c>AddWebhookEvent</c>.</summary>
/// <param name="EventClrType">The integration event contract type.</param>
/// <param name="EventType">The webhook event type name subscribers list (for example <c>ticket.opened</c>).</param>
public sealed record WebhookEventRegistration(Type EventClrType, string EventType);
