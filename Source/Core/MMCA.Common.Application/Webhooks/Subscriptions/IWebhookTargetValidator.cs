using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Webhooks.Subscriptions;

/// <summary>
/// Decides whether a webhook target may be stored: https only, no local host, and every address the
/// host resolves to is public, unless insecure targets are allowed in Development. Registered by
/// <c>AddWebhooks(configuration)</c>; the connect-time check of the delivery client stays authoritative.
/// </summary>
public interface IWebhookTargetValidator
{
    /// <summary>Validates <paramref name="target"/>.</summary>
    /// <param name="target">The absolute target URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or <c>Webhooks.TargetNotAllowed</c>.</returns>
    Task<Result> ValidateAsync(Uri target, CancellationToken cancellationToken = default);
}
