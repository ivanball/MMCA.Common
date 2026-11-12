using System.Net;
using System.Net.Sockets;
using MMCA.Common.Infrastructure.Http;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// The <see cref="SocketsHttpHandler.ConnectCallback"/> of the <c>MMCA.Webhooks</c> client: it resolves
/// the host itself and, unless insecure targets are allowed, refuses with
/// <see cref="WebhookTargetRefusedException"/> BEFORE opening a socket when any resolved address is not
/// public, then connects to the first address. Checking at connect time is what makes the policy hold
/// against DNS rebinding and against any handler that rewrites the request URI earlier in the pipeline.
/// </summary>
internal static class WebhookConnectCallback
{
    /// <summary>Creates the callback.</summary>
    /// <param name="resolver">Resolves the host.</param>
    /// <param name="allowInsecure">Whether the address check is skipped (Development with the flag set).</param>
    /// <returns>The connect callback.</returns>
    internal static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(
        IWebhookDnsResolver resolver,
        bool allowInsecure)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        return (context, cancellationToken) => ConnectAsync(resolver, allowInsecure, context.DnsEndPoint, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectAsync(
        IWebhookDnsResolver resolver,
        bool allowInsecure,
        DnsEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        var host = endPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            // Not a refusal: an empty answer may be transient, so the delivery is retried.
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (!allowInsecure && Array.Find(addresses, address => !OutboundAddressPolicy.IsPublicAddress(address)) is { } blocked)
        {
            var blockedText = blocked.ToString();
            throw new WebhookTargetRefusedException(
                $"The webhook target {host} resolved to {blockedText}, which is not a public address.");
        }

        Socket? socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], endPoint.Port), cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
