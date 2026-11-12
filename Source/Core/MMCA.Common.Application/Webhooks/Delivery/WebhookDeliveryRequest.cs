namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>What <see cref="IWebhookSender"/> posts.</summary>
/// <param name="Target">The subscription's target URL.</param>
/// <param name="WebhookId">The <c>webhook-id</c> header value.</param>
/// <param name="Body">The JSON body (<see cref="WebhookEnvelope.Build"/>), sent verbatim.</param>
/// <param name="ProtectedSecrets">The protected signing secrets, current first; one signature is sent per readable secret.</param>
public sealed record WebhookDeliveryRequest(Uri Target, string WebhookId, string Body, IReadOnlyList<string> ProtectedSecrets);
