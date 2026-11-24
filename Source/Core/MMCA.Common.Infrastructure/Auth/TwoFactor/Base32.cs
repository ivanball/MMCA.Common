namespace MMCA.Common.Infrastructure.Auth.TwoFactor;

/// <summary>
/// RFC 4648 section 6 Base32 (the upper-case <c>A-Z2-7</c> alphabet), the encoding an authenticator
/// app expects a TOTP secret in.
/// </summary>
/// <remarks>
/// Encoding always pads with <c>=</c> to a multiple of eight characters. Decoding is deliberately
/// lenient in the ways authenticator secrets are typed and stored: trailing <c>=</c> padding is
/// optional, letters are accepted in either case, and the unused low bits of the last character are
/// ignored. Any other character, including whitespace or a <c>=</c> before the end, is rejected.
/// </remarks>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encodes bytes as padded, upper-case Base32.</summary>
    /// <param name="data">The bytes to encode; must not be empty.</param>
    /// <returns>The Base32 text.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("There is nothing to encode.", nameof(data));
        }

        var length = (data.Length + 4) / 5 * 8;
        var chars = new char[length];
        var index = 0;
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            // At most four bits are left over from the previous byte, so twelve bits hold the buffer.
            buffer = ((buffer << 8) | value) & 0xFFF;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[index++] = Alphabet[(buffer >> bits) & 0x1F];
            }
        }

        if (bits > 0)
        {
            chars[index++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        }

        while (index < length)
        {
            chars[index++] = '=';
        }

        return new string(chars);
    }

    /// <summary>Decodes Base32 text, tolerating missing padding and lower-case letters.</summary>
    /// <param name="text">The Base32 text.</param>
    /// <returns>The decoded bytes; empty when the text holds fewer than eight bits.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> is null or empty, or holds a character outside the alphabet.
    /// </exception>
    public static byte[] Decode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        var trimmed = text.AsSpan().TrimEnd('=');
        var bytes = new byte[trimmed.Length * 5 / 8];
        var index = 0;
        var buffer = 0;
        var bits = 0;

        foreach (var character in trimmed)
        {
            // At most seven bits are left over from the previous character, so twelve bits suffice.
            buffer = ((buffer << 5) | ValueOf(character)) & 0xFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
            }
        }

        return bytes;
    }

    private static int ValueOf(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= 'a' and <= 'z' => character - 'a',
        >= '2' and <= '7' => character - '2' + 26,
        _ => throw new ArgumentException("The text is not Base32.", nameof(character)),
    };
}
