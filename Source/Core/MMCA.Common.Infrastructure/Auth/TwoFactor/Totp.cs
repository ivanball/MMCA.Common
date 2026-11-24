using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MMCA.Common.Infrastructure.Auth.TwoFactor;

/// <summary>
/// RFC 6238 time-based one-time passwords over HMAC-SHA1 (RFC 4226 dynamic truncation), the variant
/// every authenticator app implements.
/// </summary>
/// <remarks>
/// <para>
/// The time step is the whole Unix seconds of the instant divided by the period, so a code is live for
/// exactly one period. Verification tries the current step, then each earlier step out to the window
/// (never below step zero), then each later step, and reports the first step that matched.
/// </para>
/// <para>
/// <b>Comparison is fixed time.</b> Every candidate step is computed and compared with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>,
/// without stopping at a match, so the reply time does not say which step matched.
/// </para>
/// </remarks>
internal static class Totp
{
    /// <summary>Computes the code for the time step that contains an instant.</summary>
    /// <param name="key">The shared secret.</param>
    /// <param name="utcInstant">The instant, in UTC.</param>
    /// <param name="periodSeconds">The length of one time step, in seconds.</param>
    /// <param name="digits">The number of digits in a code.</param>
    /// <returns>The code, left-padded with zeros to <paramref name="digits"/>.</returns>
    public static string Compute(byte[] key, DateTime utcInstant, int periodSeconds, int digits) =>
        ComputeForStep(key, TimeStep(utcInstant, periodSeconds), digits);

    /// <summary>
    /// Verifies a code against the time step that contains an instant and up to
    /// <paramref name="windowSteps"/> steps either side of it.
    /// </summary>
    /// <param name="key">The shared secret.</param>
    /// <param name="code">The presented code, already normalized.</param>
    /// <param name="utcInstant">The instant, in UTC.</param>
    /// <param name="periodSeconds">The length of one time step, in seconds.</param>
    /// <param name="digits">The number of digits in a code.</param>
    /// <param name="windowSteps">How many steps before and after the current one are also accepted.</param>
    /// <param name="matchedStep">The step that matched, or zero when none did.</param>
    /// <returns><see langword="true"/> when the code matches a step inside the window.</returns>
    public static bool Verify(
        byte[] key,
        string code,
        DateTime utcInstant,
        int periodSeconds,
        int digits,
        int windowSteps,
        out long matchedStep)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(code);

        var currentStep = TimeStep(utcInstant, periodSeconds);
        var presented = MemoryMarshal.AsBytes(code.AsSpan());
        var matched = false;
        matchedStep = 0;

        foreach (var step in Candidates(currentStep, windowSteps))
        {
            var expected = ComputeForStep(key, step, digits);
            if (CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(expected.AsSpan()), presented) && !matched)
            {
                matched = true;
                matchedStep = step;
            }
        }

        return matched;
    }

    private static long TimeStep(DateTime utcInstant, int periodSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodSeconds);

        return (utcInstant.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond / periodSeconds;
    }

    private static IEnumerable<long> Candidates(long currentStep, int windowSteps)
    {
        yield return currentStep;

        for (var offset = 1; offset <= windowSteps && currentStep >= offset; offset++)
        {
            yield return currentStep - offset;
        }

        for (var offset = 1; offset <= windowSteps; offset++)
        {
            yield return currentStep + offset;
        }
    }

    private static string ComputeForStep(byte[] key, long step, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);

        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238 authenticator apps speak HMAC-SHA1; HMAC does not rely on SHA-1 collision resistance.
        HMACSHA1.HashData(key, counter, hash);
#pragma warning restore CA5350

        // RFC 4226 section 5.3 dynamic truncation: the low nibble of the last byte picks four bytes,
        // read big endian with the top bit cleared.
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;

        var modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
