namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// A scheduled local (on-device) notification. Ids must be stable per logical subject
/// (e.g. a hash of the session id) so rescheduling replaces rather than duplicates.
/// </summary>
/// <param name="Id">Stable platform notification id; scheduling the same id replaces the pending entry.</param>
/// <param name="Title">Notification title (already localized by the caller).</param>
/// <param name="Body">Notification body (already localized by the caller).</param>
/// <param name="DeliverAt">Absolute delivery time. Requests in the past are ignored.</param>
/// <param name="DeepLinkRoute">
/// Optional app-relative route (e.g. <c>/conference/sessions/42</c>) published to
/// <see cref="IDeepLinkDispatcher"/> when the user taps the notification.
/// </param>
public sealed record LocalNotificationRequest(
    int Id,
    string Title,
    string Body,
    DateTimeOffset DeliverAt,
    string? DeepLinkRoute);
