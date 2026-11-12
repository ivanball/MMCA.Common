namespace MMCA.Common.Application.Webhooks;

/// <summary>The error codes outbound webhooks return. The domain entity uses the same strings.</summary>
public static class WebhookErrorCodes
{
    /// <summary>No subscription with that id exists for the caller's tenant (NotFound, 404).</summary>
    public const string NotFound = "Webhooks.NotFound";

    /// <summary>The URL is not an absolute http(s) URL of at most 2048 characters without user info or fragment (Validation, 400).</summary>
    public const string UrlInvalid = "Webhooks.UrlInvalid";

    /// <summary>The target is not allowed: not https, a local host, or a non-public address (Validation, 400).</summary>
    public const string TargetNotAllowed = "Webhooks.TargetNotAllowed";

    /// <summary>No event type, or a blank one, was given (Validation, 400).</summary>
    public const string EventTypesRequired = "Webhooks.EventTypesRequired";

    /// <summary>More than 20 event types were given (Validation, 400).</summary>
    public const string TooManyEventTypes = "Webhooks.TooManyEventTypes";

    /// <summary>An event type is neither <c>"*"</c> nor registered with <c>AddWebhookEvent</c> (Validation, 400).</summary>
    public const string EventTypeUnknown = "Webhooks.EventTypeUnknown";

    /// <summary>The description is longer than 200 characters (Validation, 400).</summary>
    public const string DescriptionTooLong = "Webhooks.DescriptionTooLong";

    /// <summary>No signing secret was supplied (Validation, 400).</summary>
    public const string SecretRequired = "Webhooks.SecretRequired";

    /// <summary>The caller's tenant already holds the maximum number of subscriptions (Conflict, 409).</summary>
    public const string LimitReached = "Webhooks.LimitReached";

    /// <summary>A delivery failed and will be retried by the internal-command processor (Failure, queue only).</summary>
    public const string DeliveryFailed = "Webhooks.DeliveryFailed";

    /// <summary>The stored signing secret could not be unprotected on this replica (Failure, queue only).</summary>
    public const string SecretUnreadable = "Webhooks.SecretUnreadable";
}
