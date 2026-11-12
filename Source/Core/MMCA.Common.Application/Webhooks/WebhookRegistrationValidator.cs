using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Webhooks.Subscriptions;

namespace MMCA.Common.Application.Webhooks;

/// <summary>
/// Fails startup when <c>AddWebhookDelivery()</c> was called but nothing registered an
/// <see cref="IWebhookSubscriptionStore"/>: the delivery handler would fail on every row. That is a
/// wiring mistake whatever <see cref="WebhookSettings.Enabled"/> says.
/// </summary>
/// <param name="serviceProviderIsService">Answers whether the store is registered.</param>
internal sealed class WebhookRegistrationValidator(IServiceProviderIsService serviceProviderIsService)
    : IValidateOptions<WebhookSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WebhookSettings options) =>
        serviceProviderIsService.IsService(typeof(IWebhookSubscriptionStore))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Webhook delivery is registered (AddWebhookDelivery()) but no IWebhookSubscriptionStore is. "
                + "Call services.AddWebhooks(configuration) from MMCA.Common.Infrastructure.Webhooks in the host.");
}
