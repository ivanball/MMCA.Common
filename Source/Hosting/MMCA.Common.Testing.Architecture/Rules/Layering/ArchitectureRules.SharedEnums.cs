using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// The start of an enum declaration in comment- and literal-masked source: the keyword (not a
    /// member access or a verbatim identifier), the name, then anything up to the body's opening brace
    /// (an optional underlying type such as <c>: byte</c>).
    /// </summary>
    [GeneratedRegex(@"(?<![\w@.])enum\s+(?<name>@?[A-Za-z_]\w*)[^{;]*\{", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnumDeclaration { get; }

    /// <summary>The leading identifier of an enum member declaration.</summary>
    [GeneratedRegex(@"^@?[A-Za-z_]\w*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnumMemberName { get; }

    /// <summary>
    /// Wire-stable enum fitness function: every member of every enum declared in a module's Shared
    /// (contract) project carries an explicit value (<c>Pending = 0,</c>, never a bare
    /// <c>Pending,</c>). A Shared enum is a contract: it is persisted as its integer (an EF Core enum
    /// column stores the number) and crosses the wire to installed clients as a number, so the value
    /// of a member must not depend on its position. With implicit values, inserting or reordering a
    /// member silently renumbers every member after it, and rows already stored or clients already
    /// installed then read a different member than the one written. An explicit value makes that
    /// change a visible diff rather than a silent one. A flags enum with explicit power-of-two values,
    /// or a member defined in terms of others (<c>All = Read | Write</c>), passes.
    /// <para>
    /// <b>Why a source scan.</b> The compiled metadata stores a constant for every member whether or
    /// not the author wrote one, so explicit and implicit values are indistinguishable in IL or by
    /// reflection. This rule therefore reads the <c>.cs</c> files under each directory, like
    /// <see cref="FoldersStayNarrow"/>, and skips <c>bin</c> and <c>obj</c>. Comments, string and
    /// character literals and preprocessor lines are blanked before the scan, so a commented-out member
    /// or an attribute argument never decides the result; attribute sections on a member are skipped.
    /// A member the scan cannot read as an identifier is reported as unclassifiable rather than passed.
    /// </para>
    /// </summary>
    /// <param name="sharedSourceDirectories">The source directories of the module Shared projects to scan.</param>
    public static void ModuleSharedEnumValuesAreExplicit(IReadOnlyCollection<string> sharedSourceDirectories)
    {
        ArgumentNullException.ThrowIfNull(sharedSourceDirectories);

        var offenders = new List<string>();

        foreach (var directory in sharedSourceDirectories)
        {
            var displayRoot = Path.GetDirectoryName(Path.GetFullPath(directory)) ?? directory;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var display = Path.GetRelativePath(displayRoot, file).Replace('\\', '/');
                if (IsBuildOutputPath(display))
                {
                    continue;
                }

                offenders.AddRange(ImplicitEnumMembers(File.ReadAllText(file))
                    .Select(member => string.Create(
                        CultureInfo.InvariantCulture, $"  - {display}:{member.Line}: {member.Description}")));
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(offenders,
            "every member of an enum in a module Shared project carries an explicit value (Pending = 0, not a bare "
                + "Pending,): the enum is persisted and sent to installed clients as its number, so an implicit value "
                + "renumbers silently when a member is inserted or reordered");
    }

    /// <summary>True when a forward-slash path runs through a <c>bin</c> or <c>obj</c> folder.</summary>
    private static bool IsBuildOutputPath(string relativePath) =>
        relativePath.Split('/').Any(static segment => segment is "bin" or "obj");

    /// <summary>Every enum member in one source text that has no explicit value, with its line.</summary>
    /// <param name="source">The C# source text.</param>
    /// <returns>The line and a description of each implicit or unclassifiable member.</returns>
    private static IEnumerable<(int Line, string Description)> ImplicitEnumMembers(string source)
    {
        var masked = MaskNonCode(source);

        foreach (Match declaration in EnumDeclaration.Matches(masked))
        {
            var enumName = declaration.Groups["name"].Value;

            foreach (var (start, text) in EnumMemberSegments(masked, declaration.Index + declaration.Length))
            {
                var (offset, member) = SkipAttributeSections(text);
                if (member.Length == 0)
                {
                    continue;
                }

                var line = LineAt(masked, start + offset);
                var name = EnumMemberName.Match(member);
                if (!name.Success)
                {
                    yield return (line, $"{enumName} has a member the scan cannot classify ('{member.ReplaceLineEndings(" ")}'); give it an explicit value");
                }
                else if (!member.Contains('=', StringComparison.Ordinal))
                {
                    yield return (line, $"{enumName}.{name.Value} has no explicit value");
                }
            }
        }
    }

    /// <summary>
    /// Splits an enum body, starting just after its opening brace, into its comma-separated member
    /// segments, honoring nested parentheses, brackets and braces (attribute arguments) and stopping
    /// at the closing brace.
    /// </summary>
    /// <param name="masked">The masked source text.</param>
    /// <param name="bodyStart">The index just after the body's opening brace.</param>
    /// <returns>Each segment's start index and raw text.</returns>
    private static List<(int Start, string Text)> EnumMemberSegments(string masked, int bodyStart)
    {
        var segments = new List<(int Start, string Text)>();
        var depth = 0;
        var segmentStart = bodyStart;

        for (var i = bodyStart; i < masked.Length; i++)
        {
            var c = masked[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' || c == '}' && depth > 0)
            {
                depth--;
            }
            else if (c == '}' || c == ',' && depth == 0)
            {
                segments.Add((segmentStart, masked[segmentStart..i]));
                segmentStart = i + 1;
                if (c == '}')
                {
                    break;
                }
            }
        }

        return segments;
    }

    /// <summary>
    /// Skips the leading whitespace and attribute sections (<c>[...]</c>) of a member segment.
    /// </summary>
    /// <param name="segment">The raw member segment.</param>
    /// <returns>The offset of the member declaration within the segment, and its trimmed text.</returns>
    private static (int Offset, string Member) SkipAttributeSections(string segment)
    {
        var i = 0;
        while (true)
        {
            while (i < segment.Length && char.IsWhiteSpace(segment[i]))
            {
                i++;
            }

            if (i >= segment.Length || segment[i] != '[')
            {
                break;
            }

            i = AttributeSectionEnd(segment, i);
        }

        return (i, segment[i..].Trim());
    }

    /// <summary>The index just past the <c>]</c> that closes the attribute section opening at <paramref name="start"/>.</summary>
    private static int AttributeSectionEnd(string segment, int start)
    {
        var depth = 0;
        for (var i = start; i < segment.Length; i++)
        {
            depth += segment[i] switch
            {
                '[' => 1,
                ']' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return i + 1;
            }
        }

        return segment.Length;
    }

    /// <summary>The one-based line number of an index in a text.</summary>
    private static int LineAt(string text, int index) =>
        text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    /// <summary>
    /// Returns the source with every comment, string literal, character literal and preprocessor line
    /// replaced by spaces of the same length (line breaks kept), so indexes and line numbers still
    /// match the original while only code remains to be matched.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <returns>The masked text, the same length as <paramref name="source"/>.</returns>
    private static string MaskNonCode(string source)
    {
        var masked = new StringBuilder(source);
        var i = 0;
        var atLineStart = true;

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            var end = c switch
            {
                '#' when atLineStart => IndexOfLineEnd(source, i),
                '/' when next == '/' => IndexOfLineEnd(source, i),
                '/' when next == '*' => IndexAfter(source, "*/", i + 2),
                '"' or '@' or '$' => StringLiteralEnd(source, i),
                '\'' => QuotedEnd(source, i + 1, '\''),
                _ => -1,
            };

            if (end > i)
            {
                Blank(masked, i, end);
                i = end;
                atLineStart = false;
                continue;
            }

            if (c == '\n')
            {
                atLineStart = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                atLineStart = false;
            }

            i++;
        }

        return masked.ToString();
    }

    /// <summary>
    /// The index just past a string literal starting at <paramref name="start"/> (regular, verbatim,
    /// interpolated or raw), or -1 when no literal starts there.
    /// </summary>
    private static int StringLiteralEnd(string source, int start)
    {
        var i = start;
        var verbatim = false;
        while (i < source.Length && source[i] is '@' or '$')
        {
            verbatim |= source[i] == '@';
            i++;
        }

        if (i >= source.Length || source[i] != '"')
        {
            return -1;
        }

        var quotes = 0;
        while (i + quotes < source.Length && source[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // A raw string literal ends at the same run of quotes that opened it.
            return IndexAfter(source, new string('"', quotes), i + quotes);
        }

        return verbatim ? VerbatimEnd(source, i + 1) : QuotedEnd(source, i + 1, '"');
    }

    /// <summary>The index just past a backslash-escaped literal whose body starts at <paramref name="start"/>.</summary>
    private static int QuotedEnd(string source, int start, char quote)
    {
        var i = start;
        while (i < source.Length && source[i] != '\n')
        {
            if (source[i] == quote)
            {
                return i + 1;
            }

            i += source[i] == '\\' ? 2 : 1;
        }

        return Math.Min(i, source.Length);
    }

    /// <summary>The index just past a verbatim string whose body starts at <paramref name="start"/> (a doubled quote is one quote).</summary>
    private static int VerbatimEnd(string source, int start)
    {
        var i = start;
        while (i < source.Length)
        {
            if (source[i] != '"')
            {
                i++;
            }
            else if (i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
            }
            else
            {
                return i + 1;
            }
        }

        return i;
    }

    /// <summary>The index of the line break ending the line that holds <paramref name="start"/>, or the text's end.</summary>
    private static int IndexOfLineEnd(string source, int start)
    {
        var lineEnd = source.AsSpan(start).IndexOf('\n');
        return lineEnd < 0 ? source.Length : start + lineEnd;
    }

    /// <summary>The index just past the next <paramref name="token"/> at or after <paramref name="from"/>, or the text's end.</summary>
    private static int IndexAfter(string source, string token, int from)
    {
        var found = source.IndexOf(token, from, StringComparison.Ordinal);
        return found < 0 ? source.Length : found + token.Length;
    }

    /// <summary>Replaces every character in [<paramref name="from"/>, <paramref name="to"/>) except line breaks with a space.</summary>
    private static void Blank(StringBuilder text, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (text[i] is not '\n' and not '\r')
            {
                text[i] = ' ';
            }
        }
    }
}
