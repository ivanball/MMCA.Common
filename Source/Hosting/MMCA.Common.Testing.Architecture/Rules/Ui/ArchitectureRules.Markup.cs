using System.Text;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string TemplateColumnTag = "<TemplateColumn";
    private const string SortableAttributeName = "Sortable";

    /// <summary>
    /// MudBlazor's server-side sort reads the bound property off a <c>PropertyColumn</c>; a
    /// <c>TemplateColumn</c> has no bound property, so marking one <c>Sortable="true"</c> produces a
    /// clickable header that sorts nothing (or sorts the page's local slice only). The failure is
    /// silent: the grid renders, the arrow toggles, and the order is wrong. A column that must sort is
    /// a <c>PropertyColumn</c>; a <c>TemplateColumn</c> is for presentation.
    /// <para>
    /// This rule scans <c>.razor</c> TEXT rather than IL, because the defect lives in markup that
    /// compiles perfectly.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it matches.</b> Every <c>&lt;TemplateColumn ...&gt;</c> element under the given roots,
    /// with the tag read to its closing <c>&gt;</c> through quoted attribute values, so attribute
    /// order, wrapped lines and a generic <c>T="Foo&lt;Bar&gt;"</c> argument are all handled. The
    /// <c>Sortable</c> value matches the quoted or unquoted literal, with or without an <c>@</c>
    /// expression prefix and parentheses (<c>Sortable="true"</c>, <c>Sortable=@true</c>,
    /// <c>Sortable="@(true)"</c>); a value bound to a field or property is left alone, since its
    /// value is not knowable from the markup.
    /// </para>
    /// <para>
    /// <b>Commented-out markup</b> inside <c>@* *@</c> is blanked before the scan (spaces replace the
    /// comment, newlines survive), so a commented example neither fails the gate nor shifts the
    /// reported line numbers.
    /// </para>
    /// <para>
    /// <b>Limits.</b> A missing root is reported as a violation rather than silently skipped, so a
    /// path typo cannot make the gate vacuous. Files under <c>bin</c>/<c>obj</c> are excluded.
    /// </para>
    /// </remarks>
    /// <param name="markupRoots">Directories scanned recursively for <c>*.razor</c> files.</param>
    public static void SortableGridColumnsUsePropertyColumn(IReadOnlyCollection<string> markupRoots)
    {
        ArgumentNullException.ThrowIfNull(markupRoots);

        var violations = new List<string>();

        foreach (var root in markupRoots)
        {
            if (!Directory.Exists(root))
            {
                violations.Add($"  - markup root not found: {root}");
                continue;
            }

            foreach (var file in RazorFiles(root))
            {
                var markup = BlankRazorComments(File.ReadAllText(file));
                violations.AddRange(SortableTemplateColumnLines(markup)
                    .Select(line => $"  - {file}:{line} <TemplateColumn ... Sortable=\"true\">"));
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "MudDataGrid's server-side sort needs a bound property, so a sortable column must be a "
                + "PropertyColumn. A TemplateColumn marked Sortable=\"true\" renders a header that "
                + "toggles without ordering the data. Convert the column, or drop Sortable");
    }

    /// <summary>The <c>.razor</c> files under a root, excluding build output.</summary>
    private static IEnumerable<string> RazorFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Replaces every <c>@* *@</c> comment with spaces, keeping the file the same length and every
    /// newline in place so reported line numbers stay accurate.
    /// </summary>
    private static string BlankRazorComments(string markup)
    {
        var builder = new StringBuilder(markup);
        var index = 0;

        while (true)
        {
            var start = markup.IndexOf("@*", index, StringComparison.Ordinal);
            if (start < 0)
            {
                return builder.ToString();
            }

            var close = markup.IndexOf("*@", start + 2, StringComparison.Ordinal);
            var end = close < 0 ? markup.Length : close + 2;

            for (var i = start; i < end; i++)
            {
                if (builder[i] is not '\n' and not '\r')
                {
                    builder[i] = ' ';
                }
            }

            index = end;
        }
    }

    /// <summary>The 1-based line numbers of every sortable <c>TemplateColumn</c> in a markup file.</summary>
    private static IEnumerable<int> SortableTemplateColumnLines(string markup)
    {
        var index = 0;

        while (true)
        {
            var start = markup.IndexOf(TemplateColumnTag, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var afterName = start + TemplateColumnTag.Length;
            if (afterName < markup.Length && IsIdentifierCharacter(markup[afterName]))
            {
                // A longer element name that merely starts the same way, e.g. <TemplateColumnGroup.
                index = afterName;
                continue;
            }

            var end = TagEnd(markup, afterName);
            if (HasSortableTrue(markup[start..end]))
            {
                yield return LineNumberAt(markup, start);
            }

            index = end;
        }
    }

    /// <summary>The index just past a tag's closing <c>&gt;</c>, skipping <c>&gt;</c> inside quoted values.</summary>
    private static int TagEnd(string markup, int from)
    {
        var quote = '\0';

        for (var i = from; i < markup.Length; i++)
        {
            var character = markup[i];

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return i + 1;
            }
        }

        return markup.Length;
    }

    /// <summary>True when the tag carries a <c>Sortable</c> attribute whose value is literally true.</summary>
    private static bool HasSortableTrue(string tag)
    {
        var index = 0;

        while (true)
        {
            var start = tag.IndexOf(SortableAttributeName, index, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            index = start + SortableAttributeName.Length;

            // Reject a longer attribute name that merely ends the same way, e.g. IsSortable.
            if ((start == 0 || !IsIdentifierCharacter(tag[start - 1])) && IsBoundToTrue(tag, index))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// True when the attribute tail reads <c>= true</c>, allowing quotes, an <c>@</c> expression
    /// prefix, parentheses and whitespace: <c>Sortable="true"</c>, <c>Sortable=@true</c>,
    /// <c>Sortable="@(true)"</c>.
    /// </summary>
    private static bool IsBoundToTrue(string tag, int afterName)
    {
        var i = SkipWhitespace(tag, afterName);
        if (i >= tag.Length || tag[i] != '=')
        {
            return false;
        }

        i = SkipWhitespace(tag, i + 1);
        if (i < tag.Length && tag[i] is '"' or '\'')
        {
            i++;
        }

        i = SkipWhitespace(tag, i);
        while (i < tag.Length && tag[i] is '@' or '(')
        {
            i = SkipWhitespace(tag, i + 1);
        }

        if (!tag.AsSpan(i).StartsWith("true", StringComparison.Ordinal))
        {
            return false;
        }

        var after = i + "true".Length;
        return after >= tag.Length || !IsIdentifierCharacter(tag[after]);
    }

    /// <summary>The index of the first non-whitespace character at or after <paramref name="from"/>.</summary>
    private static int SkipWhitespace(string text, int from)
    {
        var i = from;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>True for a character that can continue a markup identifier.</summary>
    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    /// <summary>The 1-based line number of an index into the markup.</summary>
    private static int LineNumberAt(string markup, int index) =>
        markup.AsSpan(0, index).Count('\n') + 1;
}
