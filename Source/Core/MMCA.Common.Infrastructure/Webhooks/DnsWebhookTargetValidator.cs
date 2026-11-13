using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Infrastructure.Http;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Default <see cref="IWebhookTargetValidator"/>: applies <see cref="OutboundAddressPolicy.IsAcceptableTargetUri"/>,
/// then, unless insecure targets are allowed, resolves the host and refuses when the answer is empty or
/// ANY address is not public. With insecure targets allowed it does not resolve at all. The connect-time
/// check of the delivery client stays authoritative; this one gives the operator an early answer.
/// </summary>
/// <param name="resolver">Resolves the target host.</param>
/// <param name="settings">The webhook options.</param>
/// <param name="services">Supplies the optional host environment.</param>
internal sealed class DnsWebhookTargetValidator(
    IWebhookDnsResolver resolver,
    IOptions<WebhookSettings> settings,
    IServiceProvider services) : IWebhookTargetValidator
{
    /// <inheritdoc />
    public async Task<Result> ValidateAsync(Uri target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var allowInsecure = WebhookInsecureTargets.AreAllowed(settings.Value, services.GetService<IHostEnvironment>());
        if (!OutboundAddressPolicy.IsAcceptableTargetUri(target, allowInsecure))
        {
            return Refused("A webhook target must be an https URL whose host is not local.");
        }

        if (allowInsecure || target.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            // Insecure targets are not resolved at all; an IP literal was already judged by the policy.
            return Result.Success();
        }

        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(target.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Refused($"The webhook target host {target.DnsSafeHost} could not be resolved.");
        }

        return addresses.Length > 0 && Array.TrueForAll(addresses, OutboundAddressPolicy.IsPublicAddress)
            ? Result.Success()
            : Refused($"The webhook target host {target.DnsSafeHost} does not resolve to public addresses only.");
    }

    private static Result Refused(string message) =>
        Result.Failure(Error.Validation(WebhookErrorCodes.TargetNotAllowed, message, nameof(ValidateAsync), "Url"));
}
