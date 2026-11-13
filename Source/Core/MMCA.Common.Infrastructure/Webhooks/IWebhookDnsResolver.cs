using System.Net;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Resolves a webhook target's host, at validation time and at connect time. The default is
/// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>; tests register a stub first.
/// </summary>
internal interface IWebhookDnsResolver
{
    /// <summary>Resolves <paramref name="host"/> to its addresses.</summary>
    /// <param name="host">The host name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The addresses, possibly none.</returns>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
