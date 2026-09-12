using System.Text;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Storage;

/// <summary>
/// Turns a user-supplied file name into a blob-name segment (ADR-045). A blob name is part of a URL and
/// of a storage path, so an uploaded name never reaches it unchanged: the caller composes the final name
/// itself (a prefix, an identifier, a hash) and uses this only for the human-readable tail.
/// </summary>
public static class BlobNames
{
    /// <summary>The name used when nothing of the caller's name survives sanitization.</summary>
    private const string FallbackName = "file";

    private const int DefaultMaxLength = 100;

    /// <summary>
    /// Reduces a user-supplied file name to a blob-safe segment.
    /// </summary>
    /// <param name="fileName">The user-supplied file name.</param>
    /// <param name="maxLength">The longest result to produce, at least one character.</param>
    /// <returns>
    /// A name built only from <c>A-Z a-z 0-9 . _ -</c>: a run of any other character (spaces, path
    /// separators, accented or non-Latin letters) collapses to a single <c>-</c>, consecutive dots
    /// collapse to one so no <c>..</c> can appear, leading and trailing <c>.</c>/<c>-</c> are trimmed,
    /// the extension is lower-cased, and the stem is truncated so the whole result fits
    /// <paramref name="maxLength"/>. A name that loses everything becomes <c>file</c>. The extension is
    /// always preserved, so a result can exceed <paramref name="maxLength"/> only when the extension
    /// alone is longer than the budget.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than one.</exception>
    public static string SanitizeFileName(string fileName, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        // Trailing separators go first so the extension is read off a name that actually ends in one;
        // the stem is trimmed after the split, so a leading separator never eats the extension's dot.
        string cleaned = KeepSafeCharacters(fileName).TrimEnd('.', '-');
        if (cleaned.Length == 0)
        {
            return FallbackName;
        }

        int dot = cleaned.LastIndexOf('.');
        string stem;
        string extension;
        if (dot > 0)
        {
            stem = cleaned[..dot].Trim('.', '-');
            extension = ToLowerAscii(cleaned[dot..]);
        }
        else
        {
            stem = cleaned.Trim('.', '-');
            extension = string.Empty;
        }

        if (stem.Length == 0)
        {
            stem = FallbackName;
        }

        // The extension is never sacrificed: when it alone fills the budget the stem keeps one character.
        int budget = Math.Max(1, maxLength - extension.Length);
        if (stem.Length > budget)
        {
            // The stem has no leading '.' or '-', so trimming the cut end can never empty it.
            stem = stem[..budget].TrimEnd('.', '-');
        }

        return stem + extension;
    }

    /// <summary>
    /// Rewrites a name over the safe alphabet: runs of unsafe characters become one <c>-</c> and runs of
    /// dots become one <c>.</c>.
    /// </summary>
    private static string KeepSafeCharacters(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        foreach (char character in fileName)
        {
            char last = builder.Length == 0 ? '\0' : builder[^1];
            if (IsSafe(character))
            {
                if (character == '.' && last == '.')
                {
                    continue;
                }

                builder.Append(character);
            }
            else if (last != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Lower-cases an already-sanitized (so ASCII-only) run. Culture-aware lower-casing has nothing to
    /// decide here, and doing it by hand keeps the result independent of the host's culture.
    /// </summary>
    private static string ToLowerAscii(string value) =>
        string.Create(value.Length, value, static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                destination[index] = character is >= 'A' and <= 'Z' ? (char)(character + 'a' - 'A') : character;
            }
        });

    private static bool IsSafe(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
