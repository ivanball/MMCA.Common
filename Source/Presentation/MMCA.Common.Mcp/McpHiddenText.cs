using System.Globalization;
using System.Text;

namespace MMCA.Common.Mcp;

/// <summary>
/// Text a model reads but a reviewer does not see (R023): a Unicode format character (category Cf:
/// zero-width characters, bidi controls), a tag character (U+E0000 to U+E007F) or an HTML comment
/// opener. The endpoint runs <see cref="Find"/> over every tool, parameter and server description
/// at startup; a tool that returns third-party text (a profile, an abstract, a comment) runs
/// <see cref="Strip"/> over it before the text reaches a client, so the same characters cannot ride
/// in through a result.
/// </summary>
public static class McpHiddenText
{
    private const string HtmlCommentOpener = "<!--";

    /// <summary>Returns one finding per hidden-text occurrence in <paramref name="text"/>; empty when clean.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns>The findings, each naming the code point or the comment and its UTF-16 position.</returns>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<string> findings = [];
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsTagCharacter(rune))
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"tag character U+{rune.Value:X4} at {index}"));
            }
            else if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"format character U+{rune.Value:X4} at {index}"));
            }

            index += rune.Utf16SequenceLength;
        }

        var comment = text.IndexOf(HtmlCommentOpener, StringComparison.Ordinal);
        while (comment >= 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"HTML comment at {comment}"));
            comment = text.IndexOf(HtmlCommentOpener, comment + HtmlCommentOpener.Length, StringComparison.Ordinal);
        }

        return findings;
    }

    /// <summary>
    /// Returns <paramref name="text"/> without its invisible characters: every tag character and every
    /// Unicode format character (category Cf) is removed and everything else is kept verbatim. An
    /// HTML comment opener is left in place, because in a result it is visible text, not hidden text.
    /// Removing the Cf category also drops the zero-width joiner, so a joined emoji sequence renders
    /// as its separate parts: the price of never forwarding a character a reviewer cannot see.
    /// </summary>
    /// <param name="text">The text to clean.</param>
    /// <returns>The cleaned text; <see langword="null"/> or empty input is returned unchanged.</returns>
    public static string? Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.EnumerateRunes().Any(IsInvisible))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes().Where(static rune => !IsInvisible(rune)))
        {
            builder.Append(buffer[..rune.EncodeToUtf16(buffer)]);
        }

        return builder.ToString();
    }

    private static bool IsInvisible(Rune rune) =>
        IsTagCharacter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;

    private static bool IsTagCharacter(Rune rune) => rune.Value is >= 0xE0000 and <= 0xE007F;
}
