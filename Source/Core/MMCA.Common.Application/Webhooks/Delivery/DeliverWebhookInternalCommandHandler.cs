using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// Delivers one <see cref="DeliverWebhookInternalCommand"/>: reads the subscription with the tenant the
/// processor restored for the row, posts the signed envelope, and maps the outcome onto the
/// subscription's last-outcome columns and onto the row (success completes it, failure retries it with
/// backoff until it dead-letters). Not <c>ITransactional</c>: the outcome is recorded on its own save.
/// </summary>
/// <param name="store">Reads the subscription and records the outcome.</param>
/// <param name="sender">Posts the delivery.</param>
/// <param name="tenantContext">The tenant restored for the row.</param>
/// <param name="timeProvider">The clock for the outcome instants and the signing secrets.</param>
/// <param name="settings">The webhook options.</param>
/// <param name="logger">Receives one line per outcome.</param>
internal sealed partial class DeliverWebhookInternalCommandHandler(
    IWebhookSubscriptionStore store,
    IWebhookSender sender,
    ITenantContext tenantContext,
    TimeProvider timeProvider,
    IOptions<WebhookSettings> settings,
    ILogger<DeliverWebhookInternalCommandHandler> logger)
    : ICommandHandler<DeliverWebhookInternalCommand, Result>
{
    private const string NotPublicReason = "Target refused: not a public address.";
    private const string NotHttpsReason = "Target refused: not an https address.";

    /// <inheritdoc />
    public async Task<Result> HandleAsync(DeliverWebhookInternalCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!settings.Value.Enabled)
        {
            // The table is only mapped while webhooks are on, so a row queued before they were switched
            // off is left to retry and dead-letter, where it stays requeue-able.
            LogWebhooksDisabled(logger, command.DeliveryId);
            return Result.Failure(Error.Failure(
                WebhookErrorCodes.DeliveryFailed,
                "Webhooks are disabled in this host (Webhooks:Enabled is false).",
                nameof(HandleAsync)));
        }

        var subscription = await store.FindAsync(command.SubscriptionId, tenantContext.TenantId, cancellationToken).ConfigureAwait(false);
        if (subscription is null || subscription.IsDeleted || !subscription.IsEnabled())
        {
            LogNothingToDeliver(logger, command.DeliveryId, command.SubscriptionId);
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var request = new WebhookDeliveryRequest(
            new Uri(subscription.Url, UriKind.Absolute),
            command.DeliveryId.ToString("D"),
            WebhookEnvelope.Build(command.EventType, command.OccurredOn, command.PayloadJson),
            subscription.GetSigningSecrets(now));

        var outcome = await sender.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = ApplyOutcome(command, subscription, outcome, timeProvider.GetUtcNow().UtcDateTime);

        await store.RecordOutcomeAsync(subscription, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Maps the send outcome onto the subscription's last-outcome columns and onto the row: 2xx, 410 and
    /// a connect-time refusal complete it (retrying cannot change them); everything else fails it so the
    /// processor retries with backoff.
    /// </summary>
    private Result ApplyOutcome(
        DeliverWebhookInternalCommand command,
        WebhookSubscription subscription,
        WebhookSendOutcome outcome,
        DateTime now)
    {
        if (outcome.Status == WebhookSendStatus.Delivered)
        {
            subscription.RecordDelivered(now);
            LogDelivered(logger, command.DeliveryId, command.SubscriptionId, outcome.HttpStatusCode ?? 0);
            return Result.Success();
        }

        if (outcome.Status == WebhookSendStatus.TargetRefused)
        {
            // The tenant admin API returns LastFailure, so a refusal records a fixed reason; the detail,
            // which can name the resolved address, goes to the server-side Warning only.
            subscription.RecordFailed(now, RefusedReason(subscription));
            LogTargetRefused(logger, command.DeliveryId, command.SubscriptionId, DetailReason(outcome));
            return Result.Success();
        }

        var reason = outcome.Status is WebhookSendStatus.Rejected or WebhookSendStatus.Gone
            ? HttpReason(outcome)
            : DetailReason(outcome);
        subscription.RecordFailed(now, reason);

        if (outcome.Status == WebhookSendStatus.Gone)
        {
            subscription.Disable(now);
            LogGone(logger, command.DeliveryId, command.SubscriptionId);
            return Result.Success();
        }

        // Rejected (any other status, redirects included), TransportFailed (timeouts included) and
        // SecretUnreadable: the processor retries with backoff and dead-letters after MaxAttempts.
        LogDeliveryFailed(logger, command.DeliveryId, command.SubscriptionId, reason);
        return Result.Failure(outcome.Status == WebhookSendStatus.SecretUnreadable
            ? Error.Failure(
                WebhookErrorCodes.SecretUnreadable,
                "The webhook signing secret could not be unprotected on this replica.",
                nameof(HandleAsync))
            : Error.Failure(
                WebhookErrorCodes.DeliveryFailed,
                $"The webhook delivery failed: {reason}.",
                nameof(HandleAsync)));
    }

    private static string HttpReason(WebhookSendOutcome outcome) =>
        outcome.HttpStatusCode is { } code
            ? string.Create(CultureInfo.InvariantCulture, $"HTTP {code}")
            : DetailReason(outcome);

    /// <summary>
    /// The fixed reason recorded for a refusal. The sender refuses a target that is not https before it
    /// sends anything (the scheme rule), and refuses any other at connect time only for an address that is
    /// not public, so the stored scheme tells the two apart.
    /// </summary>
    private static string RefusedReason(WebhookSubscription subscription) =>
        subscription.Url.StartsWith(Uri.UriSchemeHttps + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase)
            ? NotPublicReason
            : NotHttpsReason;

    private static string DetailReason(WebhookSendOutcome outcome) =>
        string.IsNullOrWhiteSpace(outcome.Detail) ? outcome.Status.ToString() : outcome.Detail;

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} skipped: subscription {SubscriptionId} is missing, deleted or disabled")]
    private static partial void LogNothingToDeliver(ILogger logger, Guid deliveryId, Guid subscriptionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} to subscription {SubscriptionId} delivered (HTTP {StatusCode})")]
    private static partial void LogDelivered(ILogger logger, Guid deliveryId, Guid subscriptionId, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: the receiver answered 410 Gone, so subscription {SubscriptionId} was disabled")]
    private static partial void LogGone(ILogger logger, Guid deliveryId, Guid subscriptionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} to subscription {SubscriptionId} was refused and will not be retried: {Reason}")]
    private static partial void LogTargetRefused(ILogger logger, Guid deliveryId, Guid subscriptionId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} to subscription {SubscriptionId} failed and will be retried: {Reason}")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid deliveryId, Guid subscriptionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} not attempted: webhooks are disabled in this host (Webhooks:Enabled is false); the row will retry and dead-letter")]
    private static partial void LogWebhooksDisabled(ILogger logger, Guid deliveryId);
}
