using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Design-system rule (rubric section 20): no <c>.razor</c> file under the given roots carries an
    /// inline style, whether as an HTML <c>style=</c> attribute, a MudBlazor <c>Style=</c> parameter,
    /// a grid <c>CellStyle=</c> / <c>HeaderStyle=</c>, or any <c>*StyleFunc=</c>. Layout and sizing
    /// live as semantic classes in a stylesheet (or a scoped <c>.razor.css</c>), where a theme or a
    /// reviewer changes one declaration instead of hunting through markup. A genuinely dynamic value
    /// belongs in a class toggled from code or a CSS custom property, not a style attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it matches.</b> An attribute or component parameter whose name ends in <c>style</c> or
    /// <c>Style</c> (optionally <c>StyleFunc</c>), assigned with <c>="</c>, <c>='</c> or <c>=@</c> (a
    /// bare Razor expression). The name must not be preceded by a word character or a hyphen, so CSS
    /// text such as <c>list-style</c> and an attribute such as <c>data-style</c> stay out. Lines inside
    /// an <c>@code</c> (or <c>@functions</c>) block are C#, not markup, so a variable named
    /// <c>style</c> there is not reported; markup after the block is scanned again. Each violation
    /// reads <c>relative/path.razor:line attr=</c>.
    /// </para>
    /// <para>
    /// <b>Limits.</b> A missing root is a violation rather than a silent skip, and finding fewer than
    /// <paramref name="minimumRazorFiles"/> files fails, so a moved root cannot make the gate vacuous.
    /// Files under <c>bin</c>/<c>obj</c> are excluded. An entry in <paramref name="allowedViolations"/>
    /// exempts every violation that starts with it (a <c>path.razor</c> or a <c>path.razor:line</c>);
    /// the list is meant to stay empty.
    /// </para>
    /// </remarks>
    /// <param name="markupRoots">Directories scanned recursively for <c>*.razor</c> files.</param>
    /// <param name="minimumRazorFiles">The fewest files the scan must reach.</param>
    /// <param name="allowedViolations">Reviewed exemptions, matched as violation prefixes.</param>
    public static void RazorMarkupCarriesNoInlineStyles(
        IReadOnlyCollection<string> markupRoots,
        int minimumRazorFiles,
        IReadOnlyCollection<string>? allowedViolations = null)
    {
        ArgumentNullException.ThrowIfNull(markupRoots);

        var allowed = allowedViolations ?? [];
        var violations = new List<string>();
        var scanned = 0;

        foreach (var root in markupRoots)
        {
            if (!Directory.Exists(root))
            {
                violations.Add($"  - markup root not found: {root}");
                continue;
            }

            foreach (var file in RazorFiles(root))
            {
                scanned++;
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                violations.AddRange(InlineStyleViolations(relative, File.ReadAllLines(file))
                    .Where(v => !allowed.Any(a => v.StartsWith(a, StringComparison.Ordinal)))
                    .Select(static v => "  - " + v));
            }
        }

        if (scanned < minimumRazorFiles)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - scanned {scanned} .razor file(s), expected at least {minimumRazorFiles}: the scan must actually reach the Razor UI tree"));
        }

        ArchitectureAssert.NoViolations(violations,
            "Inline styles bypass the stylesheet. Move the declaration to a semantic class in the app's "
                + "stylesheet (or the component's scoped .razor.css) and reference it through class=, Class= or "
                + "CellClass=; a dynamic value belongs in a class toggled from code or a CSS custom property");
    }

    /// <summary>
    /// The inline-style violations in one file's lines, as <c>relativePath:line attr=</c>. Public so
    /// the detector can be pinned line by line in the rule's own tests.
    /// </summary>
    /// <param name="relativePath">The path reported for the file.</param>
    /// <param name="lines">The file's lines.</param>
    /// <returns>One entry per offending line.</returns>
    public static IEnumerable<string> InlineStyleViolations(string relativePath, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return InlineStyleViolationsIterator(relativePath, lines);
    }

    private static IEnumerable<string> InlineStyleViolationsIterator(string relativePath, IReadOnlyList<string> lines)
    {
        // Brace depth of the @code block being skipped; null while scanning markup. The block opens on
        // the directive line or the next one, and ends when its braces balance.
        int? codeDepth = null;
        var codeOpened = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (codeDepth is null && CodeBlockDirective.IsMatch(line))
            {
                codeDepth = 0;
                codeOpened = false;
            }

            if (codeDepth is { } depth)
            {
                var opens = line.Count(static c => c == '{');
                depth += opens - line.Count(static c => c == '}');
                codeOpened |= opens > 0;
                codeDepth = codeOpened && depth <= 0 ? null : depth;
                continue;
            }

            var match = InlineStyleAttribute.Match(line);
            if (match.Success)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{relativePath}:{i + 1} {match.Groups["attr"].Value}=");
            }
        }
    }

    // An attribute or component parameter whose name ends in "style" or "Style" (optionally
    // "StyleFunc"), assigned with =, whose value opens with a double quote, a single quote or a bare
    // Razor expression (@). The lookbehind keeps CSS text such as "list-style" out.
    [GeneratedRegex(@"(?<![\w-])(?<attr>[A-Za-z]*[Ss]tyle(Func)?)\s*=\s*[""'@]", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InlineStyleAttribute { get; }

    // The directive that opens a Razor component's C# block.
    [GeneratedRegex(@"^\s*@(code|functions)\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodeBlockDirective { get; }
}
