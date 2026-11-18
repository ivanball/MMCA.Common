using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Accessibility rule (rubric section 20, WCAG 2.1 AA): every routable page (a <c>.razor</c> file
    /// with an <c>@page</c> directive) gives the document exactly one level-one heading, so a screen
    /// reader can jump to the page title and axe's <c>page-has-heading-one</c> best-practice rule, which
    /// the WCAG-tag scans in <c>MMCA.Common.Testing.E2E</c> never run, would pass. A page title is a
    /// <c>MudText</c> that keeps its <c>Typo.h4</c> look and carries <c>HtmlTag="h1"</c>; without the
    /// tag MudBlazor renders an <c>h4</c> and the page has no <c>h1</c> at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Own headings.</b> A page's own h1 count is the number of <c>HtmlTag="h1"</c> parameters plus
    /// raw <c>&lt;h1&gt;</c> elements, with <c>@* *@</c> and <c>&lt;!-- --&gt;</c> comments ignored.
    /// </para>
    /// <para>
    /// <b>Heading components.</b> A page that renders one of <paramref name="headingComponents"/>
    /// (for example MMCA.Common's <c>PageHeader</c> or <c>ListPageShell</c>) gets its h1 from that
    /// component, so it must carry none of its own; a page that renders none of them must carry
    /// exactly one.
    /// </para>
    /// <para>
    /// <b>Reviewed exemptions</b>, keyed by the page path relative to its markup root (forward
    /// slashes). <paramref name="branchedHeadingPages"/> names a page whose h1 sits in mutually exclusive
    /// render branches (a loading or not-found title and the loaded title) with the exact own count it
    /// must carry; <paramref name="pagesWithoutHeading"/> names a page that renders no heading at all
    /// (an unattended redirect page). Both are checked in the other direction too: an entry that no
    /// longer names a routable page, a branched count that no longer matches, or an exempt page that
    /// has since gained a heading is itself a violation, so an exemption cannot outlive its reason.
    /// </para>
    /// <para>
    /// <b>Limits.</b> A missing root is a violation rather than a silent skip, and finding fewer than
    /// <paramref name="minimumPages"/> routable pages fails, so a moved root cannot make the gate
    /// vacuous. Files under <c>bin</c>/<c>obj</c> are excluded. Routes declared only through a
    /// <c>[Route]</c> attribute in a code-behind file are not seen.
    /// </para>
    /// </remarks>
    /// <param name="markupRoots">Directories scanned recursively for <c>*.razor</c> files.</param>
    /// <param name="minimumPages">The fewest routable pages the scan must reach.</param>
    /// <param name="headingComponents">Component tag names whose render supplies the page h1.</param>
    /// <param name="branchedHeadingPages">Pages whose h1 sits in exclusive branches, with their exact own count.</param>
    /// <param name="pagesWithoutHeading">Pages reviewed as rendering no heading at all.</param>
    public static void RoutablePagesRenderExactlyOneH1(
        IReadOnlyCollection<string> markupRoots,
        int minimumPages,
        IReadOnlyCollection<string> headingComponents,
        IReadOnlyDictionary<string, int>? branchedHeadingPages = null,
        IReadOnlyCollection<string>? pagesWithoutHeading = null)
    {
        ArgumentNullException.ThrowIfNull(markupRoots);
        ArgumentNullException.ThrowIfNull(headingComponents);

        var branched = branchedHeadingPages ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var withoutHeading = pagesWithoutHeading ?? [];
        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in markupRoots)
        {
            if (!Directory.Exists(root))
            {
                violations.Add($"  - markup root not found: {root}");
                continue;
            }

            foreach (var file in RazorFiles(root))
            {
                var razor = File.ReadAllText(file);
                if (!IsRoutableRazorPage(razor))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                seen.Add(relative);

                int? expected = null;
                if (branched.TryGetValue(relative, out var count))
                {
                    expected = count;
                }
                else if (withoutHeading.Contains(relative, StringComparer.Ordinal))
                {
                    expected = 0;
                }

                if (PageHeadingViolation(relative, razor, headingComponents, expected) is { } violation)
                {
                    violations.Add("  - " + violation);
                }

                if (expected == 0 && UsesHeadingComponent(BlankRazorComments(razor), headingComponents) is { } component)
                {
                    violations.Add($"  - {relative}: listed as rendering no heading, but it now renders <{component}>; drop the exemption");
                }
            }
        }

        violations.AddRange(branched.Keys.Concat(withoutHeading)
            .Where(page => !seen.Contains(page))
            .Order(StringComparer.Ordinal)
            .Select(static page => $"  - {page}: exempted, but no longer a routable page under the markup roots; drop the exemption"));

        if (seen.Count < minimumPages)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - scanned {seen.Count} routable page(s), expected at least {minimumPages}: the scan must actually reach the Razor UI tree"));
        }

        ArchitectureAssert.NoViolations(violations,
            "Every routable page needs exactly one h1. Add HtmlTag=\"h1\" to the page-title MudText (keep "
                + "Typo.h4), or render a component that supplies the h1 (declare it as a heading component); "
                + "list a page whose h1 sits in exclusive render branches with its exact count");
    }

    /// <summary>
    /// The heading violation of one routable page, or <see langword="null"/> when the page is
    /// compliant. Public so the detector can be pinned case by case in the rule's own tests.
    /// </summary>
    /// <param name="relativePath">The path reported for the page.</param>
    /// <param name="razor">The page markup.</param>
    /// <param name="headingComponents">Component tag names whose render supplies the page h1.</param>
    /// <param name="expectedOwnHeadings">
    /// A reviewed exact own-h1 count (a branched page, or 0 for a page exempt from carrying one), or
    /// <see langword="null"/> to apply the default: none when a heading component is rendered, else one.
    /// </param>
    /// <returns>The violation text, or <see langword="null"/>.</returns>
    public static string? PageHeadingViolation(
        string relativePath,
        string razor,
        IReadOnlyCollection<string> headingComponents,
        int? expectedOwnHeadings = null)
    {
        ArgumentNullException.ThrowIfNull(razor);
        ArgumentNullException.ThrowIfNull(headingComponents);

        var markup = HtmlComment.Replace(BlankRazorComments(razor), " ");
        var own = OwnH1.Count(markup);

        if (expectedOwnHeadings is { } expected)
        {
            return own == expected
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{relativePath}: {own} own h1 (its reviewed exemption expects exactly {expected}); update or drop the exemption");
        }

        if (UsesHeadingComponent(markup, headingComponents) is { } component)
        {
            return own == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{relativePath}: its h1 comes from <{component}>, so its {own} own h1 make a second one");
        }

        return own == 1
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{relativePath}: {own} own h1 (expected exactly one, on the page-title MudText)");
    }

    /// <summary>Whether the markup is a routable page: an <c>@page</c> directive at the start of a line.</summary>
    /// <param name="razor">The component markup (a UTF-8 BOM, which <see cref="File.ReadAllText(string)"/> strips, is tolerated).</param>
    /// <returns><see langword="true"/> for a routable page.</returns>
    public static bool IsRoutableRazorPage(string razor)
    {
        ArgumentNullException.ThrowIfNull(razor);
        return PageDirective.IsMatch(razor.TrimStart('﻿'));
    }

    private static string? UsesHeadingComponent(string markup, IReadOnlyCollection<string> headingComponents) =>
        headingComponents.FirstOrDefault(component =>
            Regex.IsMatch(markup, $@"<{Regex.Escape(component)}(?![\w.-])", RegexOptions.None, TimeSpan.FromSeconds(2)));

    // An @page directive at the start of a line.
    [GeneratedRegex(@"^@page\s", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PageDirective { get; }

    // An own level-one heading: a MudText HtmlTag="h1" (either quote) or a raw <h1> element.
    [GeneratedRegex(@"HtmlTag\s*=\s*[""']h1[""']|<h1(?=[\s>/])", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OwnH1 { get; }

    // An HTML comment, which renders nothing.
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HtmlComment { get; }
}
