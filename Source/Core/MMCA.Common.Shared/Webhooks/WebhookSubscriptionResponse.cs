using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Webhooks;

/// <summary>
/// One outbound webhook subscription as the administration surface lists it. The signing secret is
/// absent: it is returned once, at create or rotate, in <see cref="WebhookSubscriptionSecretResponse"/>.
/// </summary>
/// <param name="Id">The subscription id.</param>
/// <param name="Url">The target URL deliveries are posted to.</param>
/// <param name="Description">The operator's label, or <see langword="null"/>.</param>
/// <param name="EventTypes">The subscribed webhook event types (<c>"*"</c> for all), ordinal-sorted.</param>
/// <param name="IsEnabled">Whether deliveries are currently made.</param>
/// <param name="CreatedOn">The UTC instant the subscription was created.</param>
/// <param name="LastDeliveredOn">The UTC instant of the last successful delivery, or <see langword="null"/>.</param>
/// <param name="LastFailedOn">The UTC instant of the last failed delivery, or <see langword="null"/>.</param>
/// <param name="LastFailure">Why the last delivery failed, or <see langword="null"/>.</param>
[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "The target URL is untrusted operator input carried verbatim over the wire and validated as text (scheme, user info, fragment, length) before anything parses it; System.Uri would re-normalize it.")]
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "The target URL is untrusted operator input carried verbatim over the wire and validated as text (scheme, user info, fragment, length) before anything parses it; System.Uri would re-normalize it.")]
public sealed record WebhookSubscriptionResponse(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool IsEnabled,
    DateTime CreatedOn,
    DateTime? LastDeliveredOn,
    DateTime? LastFailedOn,
    string? LastFailure);
