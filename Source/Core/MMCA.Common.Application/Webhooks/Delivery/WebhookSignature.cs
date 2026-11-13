using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// The Standard Webhooks signing scheme: a <c>whsec_</c>-prefixed Base64 secret, and a
/// <c>v1,</c>-prefixed Base64 HMAC-SHA256 over <c>{id}.{timestamp}.{body}</c>. Receivers verify with
/// <see cref="Verify"/> or any Standard Webhooks library.
/// </summary>
public static class WebhookSignature
{
    /// <summary>The prefix of every signing secret.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>The header carrying the delivery id.</summary>
    public const string IdHeader = "webhook-id";

    /// <summary>The header carrying the Unix-seconds send time.</summary>
    public const string TimestampHeader = "webhook-timestamp";

    /// <summary>The header carrying the space-separated signatures.</summary>
    public const string SignatureHeader = "webhook-signature";

    private const string SignatureVersionPrefix = "v1,";
    private const int SecretKeyBytes = 32;

    /// <summary>Generates a new signing secret: <see cref="SecretPrefix"/> plus Base64 of 32 random bytes.</summary>
    /// <returns>The secret.</returns>
    public static string GenerateSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretKeyBytes));

    /// <summary>Decodes the HMAC key from a signing secret.</summary>
    /// <param name="secret">The secret, with its prefix.</param>
    /// <returns>The key bytes, or a <c>Webhooks.SecretUnreadable</c> failure when the secret is malformed.</returns>
    public static Result<byte[]> DecodeSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret) || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return Unreadable();
        }

        var encoded = secret.AsSpan(SecretPrefix.Length);
        var buffer = new byte[encoded.Length];
        if (encoded.IsEmpty || !Convert.TryFromBase64Chars(encoded, buffer, out var written) || written == 0)
        {
            return Unreadable();
        }

        return Result.Success(buffer[..written]);
    }

    /// <summary>Computes one signature.</summary>
    /// <param name="webhookId">The <c>webhook-id</c> value.</param>
    /// <param name="timestamp">The <c>webhook-timestamp</c> value, in Unix seconds.</param>
    /// <param name="body">The exact body sent.</param>
    /// <param name="key">The HMAC key (<see cref="DecodeSecret"/>).</param>
    /// <returns><c>v1,</c> followed by the Base64 HMAC-SHA256.</returns>
    public static string Compute(string webhookId, long timestamp, string body, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(webhookId);
        ArgumentNullException.ThrowIfNull(body);

        var signedContent = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{webhookId}.{timestamp}.{body}"));
        return SignatureVersionPrefix + Convert.ToBase64String(HMACSHA256.HashData(key, signedContent));
    }

    /// <summary>
    /// Verifies a received delivery: accepted when <paramref name="timestamp"/> lies within
    /// <paramref name="tolerance"/> of <paramref name="nowUnixSeconds"/> and any space-separated
    /// <c>v1,</c> entry of <paramref name="signatureHeader"/> matches, compared in constant time.
    /// </summary>
    /// <param name="webhookId">The <c>webhook-id</c> header.</param>
    /// <param name="timestamp">The <c>webhook-timestamp</c> header.</param>
    /// <param name="body">The raw body received.</param>
    /// <param name="signatureHeader">The <c>webhook-signature</c> header.</param>
    /// <param name="key">The HMAC key (<see cref="DecodeSecret"/>).</param>
    /// <param name="nowUnixSeconds">The receiver's current time, in Unix seconds.</param>
    /// <param name="tolerance">The largest accepted clock difference, either way.</param>
    /// <returns><see langword="true"/> when the delivery is authentic and fresh.</returns>
    public static bool Verify(
        string webhookId,
        long timestamp,
        string body,
        string signatureHeader,
        ReadOnlySpan<byte> key,
        long nowUnixSeconds,
        TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || webhookId is null || body is null)
        {
            return false;
        }

        if (!IsWithinTolerance(timestamp, nowUnixSeconds, (long)tolerance.TotalSeconds))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Compute(webhookId, timestamp, body, key));
        return signatureHeader
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.StartsWith(SignatureVersionPrefix, StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry), expected));
    }

    /// <summary>
    /// Whether <paramref name="timestamp"/> lies in [now - tolerance, now + tolerance], with both bounds
    /// saturated at the <see cref="long"/> range so no hostile value can wrap the comparison or throw.
    /// </summary>
    private static bool IsWithinTolerance(long timestamp, long nowUnixSeconds, long toleranceSeconds)
    {
        if (toleranceSeconds < 0)
        {
            return false;
        }

        var earliest = nowUnixSeconds < long.MinValue + toleranceSeconds ? long.MinValue : nowUnixSeconds - toleranceSeconds;
        var latest = nowUnixSeconds > long.MaxValue - toleranceSeconds ? long.MaxValue : nowUnixSeconds + toleranceSeconds;
        return timestamp >= earliest && timestamp <= latest;
    }

    private static Result<byte[]> Unreadable() =>
        Result.Failure<byte[]>(Error.Failure(
            WebhookErrorCodes.SecretUnreadable,
            "A webhook signing secret is the prefix whsec_ followed by a Base64 key.",
            nameof(DecodeSecret)));
}
