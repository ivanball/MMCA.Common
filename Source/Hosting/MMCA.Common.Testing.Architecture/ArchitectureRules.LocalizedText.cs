using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Anchored on a quote (plain or interpolated) immediately after the paren so variable/localized
    // arguments (Snackbar.Add(L["..."]), Snackbar.Add(message), Snackbar.Add(builder => ...)) never match.
    [GeneratedRegex(@"Snackbar\.Add\(\s*\$?""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralSnackbar { get; }

    // Matches a literal-titled page property: private (static) string Title => "Create Event";
    [GeneratedRegex(@"string\s+Title\s*=>\s*\$?""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralTitleProperty { get; }

    // Matches literal markup inside <PageTitle> (anything but a razor expression or a nested tag).
    [GeneratedRegex(@"<PageTitle>\s*[^@<\s]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralPageTitle { get; }

    // Single-line explicit form: new BreadcrumbItem("Home", ...)
    [GeneratedRegex(@"new\s+BreadcrumbItem\s*\(\s*\$?""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralBreadcrumbExplicit { get; }

    // Target-typed form inside a BreadcrumbItem collection initializer: new("Home", ...)
    [GeneratedRegex(@"new\s*\(\s*\$?""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralBreadcrumbTargetTyped { get; }

    /// <summary>
    /// Localized-text convention fitness gate (ADR-027 / rubric §27): user-visible literals must not be
    /// hard-coded in <c>.razor</c>/<c>.razor.cs</c> files — snackbar messages, page <c>Title</c>
    /// properties, <c>&lt;PageTitle&gt;</c> markup, and breadcrumb labels must resolve through
    /// <c>IStringLocalizer</c> resources. This is the regression guard that keeps "all visible text
    /// adapts to the selected language" true after the externalization sweeps: a NEW hard-coded literal
    /// fails the build instead of silently shipping English.
    /// A line carrying the marker comment <c>i18n: allow</c> is exempt (for deliberate literals such as
    /// brand names); whole files can be exempted via <paramref name="allowedFileSuffixes"/>.
    /// </summary>
    /// <param name="sourceRoot">The repo's <c>Source/</c> directory to scan.</param>
    /// <param name="allowedFileSuffixes">
    /// Relative path suffixes (compared with <c>/</c> separators) excluded from the scan entirely.
    /// </param>
    /// <param name="minimumScannedFiles">
    /// Minimum number of razor files the scan must discover — a non-vacuous guard so a wrong scan root
    /// cannot let the gate pass having checked nothing. Zero skips the guard.
    /// </param>
    public static void UserVisibleTextIsLocalized(
        string sourceRoot,
        IReadOnlyCollection<string> allowedFileSuffixes,
        int minimumScannedFiles = 0)
    {
        var razorFiles = Directory
            .EnumerateFiles(sourceRoot, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceRoot, "*.razor.cs", SearchOption.AllDirectories))
            .Where(p => !IsUnderBuildOutput(p))
            .Where(p => !allowedFileSuffixes.Any(suffix =>
                Relativize(sourceRoot, p).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

        (razorFiles.Count >= minimumScannedFiles).Should().BeTrue(
            because: $"at least {minimumScannedFiles} razor file(s) under Source/ must be discovered, so the localized-text convention is actually verified rather than passing vacuously");

        var violations = razorFiles.SelectMany(file => ScanFile(sourceRoot, file)).ToList();

        ArchitectureAssert.NoViolations(violations,
            "user-visible text must resolve through IStringLocalizer resources, not hard-coded literals "
            + "(ADR-027) — move the string into the page's .resx pair (en + es), or mark a deliberate "
            + "literal (e.g. a brand name) with an 'i18n: allow' comment on the same line");
    }

    private static IEnumerable<string> ScanFile(string sourceRoot, string path)
    {
        var relative = Relativize(sourceRoot, path);
        var lines = File.ReadAllLines(path);
        var inBreadcrumbInitializer = false;
        var inNavItemInitializer = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("i18n: allow", StringComparison.Ordinal))
            {
                continue;
            }

            // Track BreadcrumbItem / NavItem collection initializers so the target-typed
            // `new("Label", ...)` rows inside them are caught without flagging unrelated
            // target-typed news elsewhere.
            if (!inBreadcrumbInitializer && line.Contains("BreadcrumbItem>", StringComparison.Ordinal) && !line.Contains(';', StringComparison.Ordinal))
            {
                inBreadcrumbInitializer = true;
            }
            else if (inBreadcrumbInitializer && line.Contains("];", StringComparison.Ordinal))
            {
                inBreadcrumbInitializer = false;
            }

            if (!inNavItemInitializer && line.Contains("NavItem>", StringComparison.Ordinal) && !line.Contains(';', StringComparison.Ordinal))
            {
                inNavItemInitializer = true;
            }
            else if (inNavItemInitializer && line.Contains("];", StringComparison.Ordinal))
            {
                inNavItemInitializer = false;
            }

            // A NavItem row may keep its literal Title/Group when it declares a TitleResource — the
            // shared NavMenu then treats them as resource keys resolved at render time (ADR-027).
            if (inNavItemInitializer
                && LiteralBreadcrumbTargetTyped.IsMatch(line)
                && !line.Contains("TitleResource", StringComparison.Ordinal))
            {
                yield return $"  - {relative}:{i + 1} declares a NavItem with a literal title and no TitleResource";
            }

            if (LiteralSnackbar.IsMatch(line))
            {
                yield return $"  - {relative}:{i + 1} hard-codes a Snackbar message literal";
            }

            if (LiteralTitleProperty.IsMatch(line))
            {
                yield return $"  - {relative}:{i + 1} hard-codes a page Title literal";
            }

            if (LiteralPageTitle.IsMatch(line))
            {
                yield return $"  - {relative}:{i + 1} hard-codes <PageTitle> literal markup";
            }

            if (LiteralBreadcrumbExplicit.IsMatch(line)
                || inBreadcrumbInitializer && LiteralBreadcrumbTargetTyped.IsMatch(line))
            {
                yield return $"  - {relative}:{i + 1} hard-codes a BreadcrumbItem label literal";
            }
        }
    }
}
