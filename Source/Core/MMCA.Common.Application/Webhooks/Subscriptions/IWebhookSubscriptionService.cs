using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Webhooks;

namespace MMCA.Common.Application.Webhooks.Subscriptions;

/// <summary>
/// The administration operations behind <c>WebhookSubscriptionsControllerBase</c>, always scoped to the
/// caller's tenant: an id of another tenant, or of a deleted row, answers <c>Webhooks.NotFound</c>.
/// The signing secret is returned only by <see cref="CreateAsync"/> and <see cref="RotateSecretAsync"/>.
/// </summary>
public interface IWebhookSubscriptionService
{
    /// <summary>Gets a value indicating whether outbound webhooks are enabled in this host.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets the webhook event types the host publishes.</summary>
    IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>Lists the caller's subscriptions.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscriptions, without secrets.</returns>
    Task<Result<IReadOnlyList<WebhookSubscriptionResponse>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a subscription for the caller's tenant.</summary>
    /// <param name="request">The target, description and event types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription and its signing secret, shown once.</returns>
    Task<Result<WebhookSubscriptionSecretResponse>> CreateAsync(CreateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces a subscription's target, description and event types.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription, without its secret.</returns>
    Task<Result<WebhookSubscriptionResponse>> UpdateAsync(Guid id, UpdateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a new signing secret; the previous one keeps signing for the configured grace period.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription and its new secret, shown once.</returns>
    Task<Result<WebhookSubscriptionSecretResponse>> RotateSecretAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stops deliveries to a subscription.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription, without its secret.</returns>
    Task<Result<WebhookSubscriptionResponse>> DisableAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Resumes deliveries to a subscription.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription, without its secret.</returns>
    Task<Result<WebhookSubscriptionResponse>> EnableAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a subscription.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or <c>Webhooks.NotFound</c>.</returns>
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
