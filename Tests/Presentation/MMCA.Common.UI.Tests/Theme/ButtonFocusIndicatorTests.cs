using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// A visible keyboard-focus indicator on every MudBlazor button (X-03, local test run 10, WCAG 2.4.7).
/// MudBlazor 9.11 gives <c>.mud-button-root</c> (MudButton and MudIconButton) and menu activators only a
/// faint hover tint on <c>:focus-visible</c>. MMCA.Common restored a ring for links, h1, the sidebar nav
/// and the DESKTOP app bar (<c>MainLayout.razor.css</c>, scoped to <c>.appbar-container</c>), which leaves
/// two gaps: in-content buttons at every width, and the phone-width top-row icon buttons in
/// <c>div.toprow-actions</c> (the app bar is hidden below 1024px). Same regex-on-the-stylesheet approach
/// as <see cref="LinkFocusIndicatorTests"/>; the ring colour is deliberately not asserted.
/// </summary>
public sealed partial class ButtonFocusIndicatorTests
{
    private const double MinimumOutlineWidthPx = 2;

    [Theory]
    [InlineData(".mud-button-root")]
    [InlineData(".mud-menu-activator")]
    public void AppCss_GivesMudButtonsAVisibleKeyboardFocusIndicator(string buttonSelector)
    {
        var css = CssComment.Replace(ReadEmbedded("app.css"), string.Empty);
        var focusSelector = buttonSelector + ":focus-visible";

        var bodies = RuleBodies(css, selector => selector.EndsWith(focusSelector, StringComparison.Ordinal));

        bodies.Should().NotBeEmpty(
            $"X-03 (WCAG 2.4.7): app.css must declare a global `{focusSelector}` rule; the only button ring today is "
            + "scoped to `.appbar-container` in MainLayout.razor.css, so in-content MudButton/MudIconButton/menu "
            + "activators show nothing but MudBlazor's faint hover tint when focused by keyboard");
        bodies.Should().Contain(
            body => DrawsAVisibleOutline(body),
            $"X-03 (WCAG 2.4.7): the `{focusSelector}` rule in app.css must declare an outline whose style is not "
            + "none/hidden and whose width is at least 2px");
    }

    [Fact]
    public void NavMenuCss_GivesTopRowActionButtonsAVisibleKeyboardFocusIndicator()
    {
        var css = CssComment.Replace(ReadEmbedded("NavMenu.razor.css"), string.Empty);

        var bodies = RuleBodies(
            css,
            selector => selector.Contains(".toprow-actions", StringComparison.Ordinal)
                && selector.Contains(":focus-visible", StringComparison.Ordinal)
                && TopRowButtonTarget.IsMatch(selector));

        bodies.Should().NotBeEmpty(
            "X-03 (WCAG 2.4.7): below 1024px the app bar (which carries the only button focus ring) is hidden and the "
            + "CultureSwitcher/ThemeToggle/app-bar icon buttons render in `div.toprow-actions` on the dark nav, so "
            + "NavMenu.razor.css needs a `.toprow-actions ::deep .mud-button-root:focus-visible` (or `.mud-icon-button`) rule");
        bodies.Should().Contain(
            body => DrawsAVisibleOutline(body),
            "X-03 (WCAG 2.4.7): the `.toprow-actions` button :focus-visible rule must declare an outline whose style is "
            + "not none/hidden and whose width is at least 2px");
    }

    private static List<string> RuleBodies(string css, Func<string, bool> selectorMatches) =>
        [.. CssRule.Matches(css)
            .Where(rule => rule.Groups["selectors"].Value
                .Split(',')
                .Any(selector => selectorMatches(selector.Trim())))
            .Select(rule => rule.Groups["body"].Value)];

    /// <summary>
    /// True when the declarations resolve to a drawn outline: a style other than none/hidden (from the
    /// <c>outline</c> shorthand or <c>outline-style</c>) and a width of at least 2px (from the shorthand or
    /// <c>outline-width</c>; a shorthand with no width uses the initial <c>medium</c>).
    /// </summary>
    private static bool DrawsAVisibleOutline(string body)
    {
        string? style = null;
        double? width = null;

        foreach (Match declaration in Declaration.Matches(body))
        {
            var property = declaration.Groups["property"].Value.Trim().ToUpperInvariant();
            var value = Important.Replace(declaration.Groups["value"].Value, string.Empty).Trim();
            var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            switch (property)
            {
                case "OUTLINE":
                    style = tokens.FirstOrDefault(IsOutlineStyle) ?? "none";
                    width = tokens.Select(ParseWidth).FirstOrDefault(w => w is not null) ?? ParseWidth("medium");
                    break;
                case "OUTLINE-STYLE":
                    style = tokens.FirstOrDefault();
                    break;
                case "OUTLINE-WIDTH":
                    width = tokens.Select(ParseWidth).FirstOrDefault(w => w is not null);
                    break;
                default:
                    break;
            }
        }

        width ??= ParseWidth("medium");
        return style is not null
            && !string.Equals(style, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(style, "hidden", StringComparison.OrdinalIgnoreCase)
            && width >= MinimumOutlineWidthPx;
    }

    private static bool IsOutlineStyle(string token) =>
        OutlineStyleKeyword.IsMatch(token);

    private static double? ParseWidth(string token)
    {
        switch (token.ToUpperInvariant())
        {
            case "THIN":
                return 1;
            case "MEDIUM":
                return 3;
            case "THICK":
                return 5;
            case "0":
                return 0;
            default:
                break;
        }

        var match = Length.Match(token);
        if (!match.Success)
        {
            return null;
        }

        var number = double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        return string.Equals(match.Groups["unit"].Value, "px", StringComparison.OrdinalIgnoreCase)
            ? number
            : number * 16;
    }

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(ButtonFocusIndicatorTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CssComment { get; }

    // A flat rule: selector list, then a declaration block with no nested braces. A rule inside an
    // at-rule block still matches, starting after the at-rule's opening brace.
    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CssRule { get; }

    [GeneratedRegex(@"(?<property>[a-zA-Z-]+)\s*:\s*(?<value>[^;]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Declaration { get; }

    [GeneratedRegex(@"!\s*important", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Important { get; }

    [GeneratedRegex(@"^(none|hidden|auto|solid|dashed|dotted|double|groove|ridge|inset|outset)$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OutlineStyleKeyword { get; }

    [GeneratedRegex(@"^(?<number>\d+(\.\d+)?)(?<unit>px|rem|em)$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Length { get; }

    // The top-row actions are MudIconButtons (and MudMenu activators wrapping them).
    [GeneratedRegex(@"\.mud-button-root|\.mud-icon-button|\.mud-menu-activator|\bbutton\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TopRowButtonTarget { get; }
}
