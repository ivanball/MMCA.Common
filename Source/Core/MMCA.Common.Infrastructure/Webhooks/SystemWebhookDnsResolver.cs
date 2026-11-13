using System.Net;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>The default <see cref="IWebhookDnsResolver"/>, over the system resolver.</summary>
internal sealed class SystemWebhookDnsResolver : IWebhookDnsResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}
