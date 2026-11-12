using MMCA.Common.Domain.Webhooks;

namespace MMCA.Common.Application.Webhooks.Subscriptions;

/// <summary>
/// Reads and writes <see cref="WebhookSubscription"/> rows. Every read filters by tenant, a
/// <see langword="null"/> tenant matching only untenanted rows, and never returns a soft-deleted row.
/// Registered by <c>AddWebhooks(configuration)</c>.
/// </summary>
public interface IWebhookSubscriptionStore
{
    /// <summary>Lists the tenant's subscriptions.</summary>
    /// <param name="tenantId">The tenant, or <see langword="null"/> for untenanted rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscriptions.</returns>
    Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Lists the tenant's enabled subscriptions, for the fan-out.</summary>
    /// <param name="tenantId">The tenant, or <see langword="null"/> for untenanted rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enabled subscriptions.</returns>
    Task<IReadOnlyList<WebhookSubscription>> ListEnabledAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Finds one of the tenant's subscriptions, tracked for an update.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="tenantId">The tenant, or <see langword="null"/> for untenanted rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription, or <see langword="null"/>.</returns>
    Task<WebhookSubscription?> FindAsync(Guid id, string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Counts the tenant's subscriptions.</summary>
    /// <param name="tenantId">The tenant, or <see langword="null"/> for untenanted rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of non-deleted subscriptions.</returns>
    Task<int> CountAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Adds and saves a new subscription.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Saves an administrative change to a subscription returned by <see cref="FindAsync"/>.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a delivery's last-outcome columns. A concurrency conflict is swallowed: a concurrent
    /// administrative change or delivery outcome already moved the row.
    /// </summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RecordOutcomeAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default);
}
