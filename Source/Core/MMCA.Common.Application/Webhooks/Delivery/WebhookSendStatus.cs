namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>How one webhook POST ended.</summary>
public enum WebhookSendStatus
{
    /// <summary>The receiver answered 2xx.</summary>
    Delivered = 0,

    /// <summary>The receiver answered any other status (redirects included); the delivery is retried.</summary>
    Rejected = 1,

    /// <summary>The receiver answered 410 Gone; the subscription is disabled.</summary>
    Gone = 2,

    /// <summary>
    /// The target was refused: an address that is not public at connect time, or a scheme this host does
    /// not accept at send time. Retrying cannot change it.
    /// </summary>
    TargetRefused = 3,

    /// <summary>The request failed in transport (timeouts included); the delivery is retried.</summary>
    TransportFailed = 4,

    /// <summary>The current signing secret could not be unprotected; nothing was sent and the delivery is retried.</summary>
    SecretUnreadable = 5,
}
