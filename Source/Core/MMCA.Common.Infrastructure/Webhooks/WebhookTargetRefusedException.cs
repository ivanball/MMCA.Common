namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Thrown by the webhook client's connect callback, before any socket is opened, when the target host
/// resolves to an address that is not public. The sender maps it to <c>TargetRefused</c>.
/// </summary>
internal sealed class WebhookTargetRefusedException : HttpRequestException
{
    /// <summary>Initializes a new instance of the <see cref="WebhookTargetRefusedException"/> class.</summary>
    public WebhookTargetRefusedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebhookTargetRefusedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public WebhookTargetRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebhookTargetRefusedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public WebhookTargetRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
