using System.Net;

namespace MMCA.Common.Azure.Notifications;

/// <summary>The push platforms an installation can target through the hub.</summary>
internal enum NotificationHubPlatform
{
    /// <summary>Firebase Cloud Messaging HTTP v1 (Android); <c>fcmV1</c> on the wire.</summary>
    FcmV1,

    /// <summary>Apple Push Notification service; <c>apns</c> on the wire.</summary>
    Apns,
}

/// <summary>
/// A hub installation as the registrar writes it: the client-owned id, the platform, the device's
/// push channel and the tags (the owner's <c>user:{id}</c> stamp).
/// </summary>
internal sealed record NotificationHubInstallation(
    string InstallationId,
    NotificationHubPlatform Platform,
    string? PushChannel,
    IReadOnlyList<string>? Tags);

/// <summary>An installation as the hub returns it; only the members the registrar reads.</summary>
internal sealed record NotificationHubInstallationState(string? PushChannel, IReadOnlyList<string>? Tags);

/// <summary>
/// A native notification: the <c>ServiceBusNotification-Format</c> value that picks the platform and
/// the platform's own JSON payload.
/// </summary>
internal sealed record NotificationHubNotification
{
    private NotificationHubNotification(string format, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        Format = format;
        Body = body;
    }

    /// <summary>The <c>ServiceBusNotification-Format</c> header value.</summary>
    public string Format { get; }

    /// <summary>The platform payload, sent as <c>application/json</c>.</summary>
    public string Body { get; }

    /// <summary>An FCM v1 native notification (format <c>fcmv1</c>).</summary>
    public static NotificationHubNotification FcmV1(string jsonPayload) => new("fcmv1", jsonPayload);

    /// <summary>An APNs native notification (format <c>apple</c>).</summary>
    public static NotificationHubNotification Apple(string jsonPayload) => new("apple", jsonPayload);
}

/// <summary>
/// One page of registrations: each registration's tags (system tags such as
/// <c>$InstallationId:{id}</c> included) and the continuation token for the next page, null or empty
/// on the last one.
/// </summary>
internal sealed record NotificationHubRegistrationPage(
    IReadOnlyList<IReadOnlySet<string>> RegistrationTags,
    string? ContinuationToken);

/// <summary>
/// A failed hub request: a non-success status, or a transport failure before any status arrived.
/// <see cref="IsEntityNotFound"/> is the not-found answer the registrar treats as "already gone";
/// <see cref="IsTransient"/> and <see cref="RetryAfter"/> drive the client's retry.
/// </summary>
#pragma warning disable CA1032, CA1064, RCS1194, S3871 // Internal by design: only the hub client raises it and only the registrar and sender catch it, so it adds nothing to the public surface; the standard constructors would be dead code.
internal sealed class NotificationHubException : Exception
#pragma warning restore CA1032, CA1064, RCS1194, S3871
{
    internal NotificationHubException(string message, HttpStatusCode? statusCode, bool isTransient, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    /// <summary>The response status, or null for a transport failure.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Whether a retry may succeed (server busy, throttled, a timed-out connection).</summary>
    public bool IsTransient { get; }

    /// <summary>The wait the hub asked for before a retry, when it asked.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The entity (installation) does not exist: a 404, or a 204 where content was expected.</summary>
    public bool IsEntityNotFound => StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent;
}
