using System.Security.Cryptography;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// The deterministic <c>webhook-id</c> of one (event, subscription) pair, so a redelivered event
/// schedules the same id and a receiver can dedupe on it.
/// </summary>
public static class WebhookDeliveryId
{
    /// <summary>
    /// The first 16 bytes of SHA-256 over <paramref name="eventId"/>'s bytes followed by
    /// <paramref name="subscriptionId"/>'s bytes (<see cref="Guid.ToByteArray()"/> order), as a <see cref="Guid"/>.
    /// </summary>
    /// <param name="eventId">The integration event's <c>MessageId</c>.</param>
    /// <param name="subscriptionId">The subscription id.</param>
    /// <returns>The delivery id.</returns>
    public static Guid For(Guid eventId, Guid subscriptionId)
    {
        Span<byte> input = stackalloc byte[32];
        eventId.TryWriteBytes(input[..16]);
        subscriptionId.TryWriteBytes(input[16..]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
