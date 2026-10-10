using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// A visible keyboard-focus indicator on every link (X-03, local test run 9, WCAG 2.4.7). MudBlazor
/// 9.11 ships <c>a:focus-visible{outline:none}</c>, and MMCA.Common restored a ring only for h1, the
/// sidebar nav links and the app bar, so a link rendered with Underline.Always or Underline.None
/// (the footer legal links, ExternalLink, the auth page links) showed no focus indicator at all.
/// The global stylesheet <c>wwwroot/app.css</c> loads after MudBlazor.min.css, so a rule there
/// with the same specificity wins the cascade; this asserts the rule exists for both a plain
/// <c>a</c> and a MudLink (<c>.mud-link</c>) and that it actually draws something.
/// </summary>
public sealed partial class LinkFocusIndicatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData(".mud-link")]
    public void AppCss_GivesLinksAVisibleKeyboardFocusIndicator(string linkSelector)
    {
        var css = CssComment.Replace(ReadAppCss(), string.Empty);
        var focusSelector = linkSelector + ":focus-visible";

        var bodies = CssRule.Matches(css)
            .Where(rule => rule.Groups["selectors"].Value
                .Split(',')
                .Any(selector => string.Equals(selector.Trim(), focusSelector, StringComparison.Ordinal)))
            .Select(rule => rule.Groups["body"].Value)
            .ToList();

        bodies.Should().NotBeEmpty(
            $"X-03 (WCAG 2.4.7): app.css must declare a `{focusSelector}` rule, because MudBlazor 9.11 ships "
            + "`a:focus-visible` with `outline: none` and links with Underline.Always/None (footer legal links, ExternalLink, "
            + "auth page links) otherwise show no keyboard focus indicator; app.css loads after MudBlazor.min.css, so the "
            + "same-specificity rule there wins");
        bodies.Should().Contain(
            body => DrawsAVisibleIndicator(body),
            $"X-03 (WCAG 2.4.7): the `{focusSelector}` rule in app.css must draw a visible indicator: an `outline` "
            + "(or `outline-style`) other than none/0, or a non-none `box-shadow`");
    }

    private static bool DrawsAVisibleIndicator(string body) =>
        Declaration.Matches(body).Any(declaration =>
        {
            var property = declaration.Groups["property"].Value;
            var value = declaration.Groups["value"].Value.Trim();
            var isNone = value.StartsWith("none", StringComparison.OrdinalIgnoreCase);
            return property.ToUpperInvariant() switch
            {
                "OUTLINE" => !NoneOrZero.IsMatch(value),
                "OUTLINE-STYLE" => !isNone,
                "BOX-SHADOW" => !isNone,
                _ => false,
            };
        });

    private static string ReadAppCss()
    {
        using var stream = typeof(LinkFocusIndicatorTests).Assembly.GetManifestResourceStream("app.css")
            ?? throw new InvalidOperationException("app.css must be embedded as a resource");
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

    [GeneratedRegex(@"^(none|0(px|rem|em)?)(\s|!|$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoneOrZero { get; }
}
