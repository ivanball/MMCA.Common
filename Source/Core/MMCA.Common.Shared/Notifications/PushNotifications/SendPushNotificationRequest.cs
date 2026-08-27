namespace MMCA.Common.Shared.Notifications.PushNotifications;

/// <summary>
/// Request record for sending a push notification to all recipients.
/// </summary>
public sealed record SendPushNotificationRequest(string Title, string Body)
{
    /// <summary>
    /// Maximum length of <see cref="Title"/>, mirroring the domain invariant
    /// (<c>PushNotificationInvariants.TitleMaxLength</c>) and the server-side request validator.
    /// It lives on the contract because the contract is the one type both sides of the wire share:
    /// a compose form can cap its input and draw its character counter from the same number the
    /// server rejects on, instead of restating it.
    /// </summary>
    public const int TitleMaxLength = 200;

    /// <summary>
    /// Maximum length of <see cref="Body"/>, mirroring the domain invariant
    /// (<c>PushNotificationInvariants.BodyMaxLength</c>) and the server-side request validator.
    /// </summary>
    public const int BodyMaxLength = 2000;

    /// <summary>
    /// Gets the optional scope key the notification is stamped with (for example <c>"event:2"</c>).
    /// It is an init property rather than a positional parameter so every existing caller keeps
    /// compiling. Null (the default) sends an unscoped notification, visible to every read.
    /// </summary>
    public string? ScopeKey { get; init; }
}
