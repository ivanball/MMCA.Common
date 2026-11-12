namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>The result of one webhook POST.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="HttpStatusCode">The receiver's status code, when it answered.</param>
/// <param name="Detail">The failure detail (<c>"ExceptionType: message"</c>), when there is one.</param>
public sealed record WebhookSendOutcome(WebhookSendStatus Status, int? HttpStatusCode, string? Detail);
