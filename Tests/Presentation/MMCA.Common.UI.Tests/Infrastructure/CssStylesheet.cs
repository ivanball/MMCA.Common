using System.Text.RegularExpressions;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Reads a stylesheet embedded in this test assembly (the <c>EmbeddedResource</c> items in the csproj)
/// and splits it with the regexes the stylesheet-assertion tests share. Rule-specific checks (what
/// counts as a visible focus indicator, which selector a test targets) stay in each test class.
/// </summary>
internal static partial class CssStylesheet
{
    /// <summary>Reads the embedded resource with the given logical name as text.</summary>
    public static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(CssStylesheet).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The declaration blocks of every flat rule in <paramref name="css"/> with at least one selector
    /// (trimmed) that <paramref name="selectorMatches"/> accepts.
    /// </summary>
    public static List<string> RuleBodies(string css, Func<string, bool> selectorMatches) =>
        [.. CssRule.Matches(css)
            .Where(rule => rule.Groups["selectors"].Value
                .Split(',')
                .Any(selector => selectorMatches(selector.Trim())))
            .Select(rule => rule.Groups["body"].Value)
        ];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    public static partial Regex CssComment { get; }

    // A flat rule: selector list, then a declaration block with no nested braces. A rule inside an
    // at-rule block still matches, starting after the at-rule's opening brace.
    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex CssRule { get; }

    [GeneratedRegex(@"(?<property>[a-zA-Z-]+)\s*:\s*(?<value>[^;]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex Declaration { get; }
}
