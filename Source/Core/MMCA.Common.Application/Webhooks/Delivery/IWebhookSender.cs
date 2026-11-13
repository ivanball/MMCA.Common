namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>Posts one signed webhook delivery. Registered by <c>AddWebhooks(configuration)</c>.</summary>
public interface IWebhookSender
{
    /// <summary>Signs and posts <paramref name="request"/>; never reads the response body.</summary>
    /// <param name="request">The delivery.</param>
    /// <param name="cancellationToken">Cancellation token; cancellation propagates.</param>
    /// <returns>How the POST ended.</returns>
    Task<WebhookSendOutcome> SendAsync(WebhookDeliveryRequest request, CancellationToken cancellationToken = default);
}
