using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

// The test-gap traceability rules (ADR-147): handlers named by a test, pages reached by an E2E
// literal, and manual-case traits that resolve to a real case. All three read SOURCE text, not IL or
// test assemblies, so an architecture test project needs no reference to the projects it checks.
// Matching is deliberately simple and therefore approximate (a name in a comment counts); the coverage
// ratchet is the check that measures execution.
public static partial class ArchitectureRules
{
    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierRegex { get; }

    [GeneratedRegex("""^\s*@page\s+"(?<route>[^"]+)"\s*$""", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PageDirectiveRegex { get; }

    [GeneratedRegex("""\$?@?"(?<text>(?:[^"\\\r\n]|\\.)*)"(?!")""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StringLiteralRegex { get; }

    [GeneratedRegex(@"^(?:\{[^}]*\})+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingInterpolationRegex { get; }

    [GeneratedRegex(@"^###\s+(?<id>\S+)\s", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CaseHeadingRegex { get; }

    [GeneratedRegex("""Trait\s*\(\s*"Case"\s*,\s*"(?<id>[^"]+)"\s*\)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CaseTraitRegex { get; }

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteParameterRegex { get; }

    /// <summary>
    /// Every command and query handler in the map's per-module Application assemblies must be named,
    /// as a whole word, in at least one <c>*.cs</c> file under <paramref name="testsRoots"/>. An
    /// allowlist entry that names no handler is reported as stale, so the list can only shrink.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="testsRoots">Repo-relative folders (forward slashes) whose source files count as tests.</param>
    /// <param name="allowedUntestedHandlers">Handler type full names exempt from the rule.</param>
    public static void HandlersHaveTests(
        IArchitectureMap map,
        string repoRoot,
        IReadOnlyCollection<string> testsRoots,
        IReadOnlyCollection<string> allowedUntestedHandlers)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(testsRoots);
        ArgumentNullException.ThrowIfNull(allowedUntestedHandlers);

        var handlers = HandlerTypes(map);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in testsRoots.SelectMany(root => ScanFiles(repoRoot, root, "*.cs")))
        {
            foreach (Match identifier in IdentifierRegex.Matches(File.ReadAllText(file)))
            {
                named.Add(identifier.Value);
            }
        }

        var handlerNames = handlers.Select(h => h.FullName ?? h.Name).ToHashSet(StringComparer.Ordinal);
        var violations = handlers
            .Where(h => !allowedUntestedHandlers.Contains(h.FullName ?? h.Name) && !named.Contains(SimpleName(h)))
            .Select(h => $"  - {h.FullName} has no test (no file under {string.Join(", ", testsRoots)} names {SimpleName(h)})")
            .Concat(allowedUntestedHandlers
                .Where(entry => !handlerNames.Contains(entry))
                .Select(entry => $"  - {entry} is allowlisted but names no handler (stale: remove it)"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every command and query handler ships with a test that names it (ADR-147): write one, or "
                + "allowlist the handler with the reason it needs none");
    }

    /// <summary>The number of handlers <see cref="HandlersHaveTests"/> inspects, for its non-vacuity floor.</summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <returns>The count of distinct concrete command and query handler types.</returns>
    public static int HandlerCount(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return HandlerTypes(map).Count;
    }

    /// <summary>
    /// Every <c>@page</c> route under <paramref name="sourceRoot"/> must be matched by a string literal
    /// in the E2E sources. A template segment <c>{param}</c> (any constraint) matches one path segment,
    /// <c>{param?}</c> an optional one and <c>{*rest}</c> the remainder; a literal's query string,
    /// fragment and leading interpolation holes (<c>{BaseUrl}</c>) are ignored, and matching ignores
    /// case and a trailing slash.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="sourceRoot">The repo-relative folder holding the <c>.razor</c> pages.</param>
    /// <param name="e2eSourceRoots">Repo-relative folders holding the E2E tests and page objects.</param>
    /// <param name="allowedUntestedRoutes">Route templates exempt from the rule, spelled as in their directive.</param>
    public static void PagesHaveE2ETests(
        string repoRoot,
        string sourceRoot,
        IReadOnlyCollection<string> e2eSourceRoots,
        IReadOnlyCollection<string> allowedUntestedRoutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(e2eSourceRoots);
        ArgumentNullException.ThrowIfNull(allowedUntestedRoutes);

        var literals = E2EPathLiterals(repoRoot, e2eSourceRoots);

        var violations = PageRoutes(repoRoot, sourceRoot)
            .Where(page => !allowedUntestedRoutes.Contains(page.Template, StringComparer.Ordinal))
            .Where(page => !IsReachedByALiteral(page.Template, literals))
            .Select(page => $"  - {page.Template} ({page.File}) is reached by no E2E test literal")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every page ships with an end-to-end test that opens it (ADR-147): add one, or allowlist the "
                + "route with the reason an E2E run cannot reach it");
    }

    /// <summary>
    /// Every entry of a page E2E-coverage allowlist must still earn its place: it names a route
    /// template some <c>@page</c> directive under <paramref name="sourceRoot"/> still declares, AND that
    /// route is still reached by no E2E literal. An entry for a renamed or deleted page, or for a page
    /// a test now opens, is stale: it would silently exempt whatever reuses the template next.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="sourceRoot">The repo-relative folder holding the <c>.razor</c> pages.</param>
    /// <param name="e2eSourceRoots">Repo-relative folders holding the E2E tests and page objects.</param>
    /// <param name="allowedUntestedRoutes">Route templates exempt from the rule, spelled as in their directive.</param>
    public static void PageE2EAllowlistHasNoStaleEntries(
        string repoRoot,
        string sourceRoot,
        IReadOnlyCollection<string> e2eSourceRoots,
        IReadOnlyCollection<string> allowedUntestedRoutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(e2eSourceRoots);
        ArgumentNullException.ThrowIfNull(allowedUntestedRoutes);

        var templates = PageRoutes(repoRoot, sourceRoot)
            .Select(page => page.Template)
            .ToHashSet(StringComparer.Ordinal);
        var literals = E2EPathLiterals(repoRoot, e2eSourceRoots);

        var missing = allowedUntestedRoutes
            .Where(route => !templates.Contains(route))
            .Select(route => $"  - {route} is allowlisted but no @page directive under {sourceRoot} declares it");
        var nowCovered = allowedUntestedRoutes
            .Where(route => templates.Contains(route) && IsReachedByALiteral(route, literals))
            .Select(route => $"  - {route} is allowlisted but an E2E test now reaches it");

        var violations = missing
            .Concat(nowCovered)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every allowlisted route still names an existing page that no E2E test reaches (ADR-147): "
                + "remove the stale entry");
    }

    /// <summary>The number of <c>@page</c> routes under the source root, for the non-vacuity floor.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="sourceRoot">The repo-relative folder holding the <c>.razor</c> pages.</param>
    /// <returns>The count of route directives (a page declaring two routes counts twice).</returns>
    public static int PageRouteCount(string repoRoot, string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);

        return PageRoutes(repoRoot, sourceRoot).Count;
    }

    /// <summary>
    /// Every <c>[Trait("Case", id)]</c> under <paramref name="testsRoot"/> must name a case that exists
    /// as a <c>### id </c> heading in the Markdown files of <paramref name="suiteFolder"/>. A missing
    /// suite folder is a failure: there is nothing to resolve the links against.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="testsRoot">The repo-relative folder scanned for traits.</param>
    /// <param name="suiteFolder">The repo-relative folder holding the manual suite.</param>
    public static void ManualCaseTraitsResolve(string repoRoot, string testsRoot, string suiteFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(testsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteFolder);

        if (!Directory.Exists(Path.Combine(repoRoot, suiteFolder)))
        {
            ArchitectureAssert.NoViolations(
                [$"  - the manual suite folder {suiteFolder} does not exist under {repoRoot}"],
                "a Case trait can only be checked against the repo's manual suite");
            return;
        }

        var cases = SuiteCaseIds(repoRoot, suiteFolder);
        var violations = CaseTraits(repoRoot, testsRoot)
            .Where(link => !cases.Contains(link.Id))
            .Select(link => $"  - {link.Id}: {link.File} links no case in {suiteFolder}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "a [Trait(\"Case\", id)] names a manual test case that exists (ADR-147): fix the id, or add "
                + "the case to the suite");
    }

    /// <summary>The number of distinct suite cases linked by at least one resolvable trait.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="testsRoot">The repo-relative folder scanned for traits.</param>
    /// <param name="suiteFolder">The repo-relative folder holding the manual suite.</param>
    /// <returns>The count of linked cases, or 0 when the suite folder is missing.</returns>
    public static int LinkedManualCaseCount(string repoRoot, string testsRoot, string suiteFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(testsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteFolder);

        if (!Directory.Exists(Path.Combine(repoRoot, suiteFolder)))
        {
            return 0;
        }

        var cases = SuiteCaseIds(repoRoot, suiteFolder);
        return CaseTraits(repoRoot, testsRoot)
            .Select(link => link.Id)
            .Where(cases.Contains)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>The distinct concrete command and query handler types in the map's module Application assemblies.</summary>
    private static List<Type> HandlerTypes(IArchitectureMap map) =>
        [.. map.ModuleApplication()
            .Distinct()
            .SelectMany(assembly => assembly.LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && !t.ContainsGenericParameters && IsHandler(t))
            .Distinct()
        ];

    /// <summary>The type name as source spells it: no generic arity suffix.</summary>
    private static string SimpleName(Type type)
    {
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? type.Name : type.Name[..tick];
    }

    private static List<(string Template, string File)> PageRoutes(string repoRoot, string sourceRoot) =>
        [.. ScanFiles(repoRoot, sourceRoot, "*.razor")
            .SelectMany(file => PageDirectiveRegex
                .Matches(File.ReadAllText(file))
                .Select(m => (m.Groups["route"].Value, Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))))
        ];

    /// <summary>A literal reduced to the path it navigates to, or null when it is not a path.</summary>
    /// <summary>
    /// Every string literal in the E2E sources that reads as an app path (normalized: query, fragment
    /// and leading interpolation holes removed), distinct.
    /// </summary>
    private static List<string> E2EPathLiterals(string repoRoot, IReadOnlyCollection<string> e2eSourceRoots) =>
    [
        .. e2eSourceRoots
            .SelectMany(root => ScanFiles(repoRoot, root, "*.cs"))
            .SelectMany(file => StringLiteralRegex.Matches(File.ReadAllText(file)).Select(m => NormalizeLiteral(m.Groups["text"].Value)))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>Whether some E2E path literal matches the route template.</summary>
    private static bool IsReachedByALiteral(string template, List<string> literals)
    {
        var pattern = RouteTemplatePattern(template);
        return literals.Exists(pattern.IsMatch);
    }

    private static string? NormalizeLiteral(string literal)
    {
        var path = LeadingInterpolationRegex.Replace(literal, string.Empty);
        if (path is not ['/', ..])
        {
            return null;
        }

        var cut = path.IndexOfAny(['?', '#']);
        return cut < 0 ? path : path[..cut];
    }

    /// <summary>Compiles a route template into a whole-path, case-insensitive pattern.</summary>
    private static Regex RouteTemplatePattern(string template)
    {
        var pattern = new System.Text.StringBuilder("^");
        foreach (var segment in template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("{*", StringComparison.Ordinal) || segment.StartsWith("{**", StringComparison.Ordinal))
            {
                pattern.Append("(?:/.*)?");
                break;
            }

            if (segment is ['{', ..] && segment.EndsWith("?}", StringComparison.Ordinal))
            {
                pattern.Append("(?:/[^/]+)?");
                continue;
            }

            pattern.Append('/');
            var position = 0;
            foreach (Match parameter in RouteParameterRegex.Matches(segment))
            {
                pattern.Append(Regex.Escape(segment[position..parameter.Index])).Append("[^/]+");
                position = parameter.Index + parameter.Length;
            }

            pattern.Append(Regex.Escape(segment[position..]));
        }

        pattern.Append("/?$");
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static HashSet<string> SuiteCaseIds(string repoRoot, string suiteFolder) =>
        [.. ScanFiles(repoRoot, suiteFolder, "*.md")
            .SelectMany(file => CaseHeadingRegex.Matches(File.ReadAllText(file)).Select(m => m.Groups["id"].Value))
        ];

    private static List<(string Id, string File)> CaseTraits(string repoRoot, string testsRoot) =>
        [.. ScanFiles(repoRoot, testsRoot, "*.cs")
            .SelectMany(file => CaseTraitRegex
                .Matches(File.ReadAllText(file))
                .Select(m => (m.Groups["id"].Value, Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))))
        ];

    /// <summary>Files under a repo-relative folder, skipping build output and tool-owned trees.</summary>
    private static IEnumerable<string> ScanFiles(string repoRoot, string relativeFolder, string pattern)
    {
        var folder = Path.Combine(repoRoot, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(repoRoot, file)
                .Replace('\\', '/')
                .Split('/')
                .Any(static segment => segment is "bin" or "obj" or "node_modules" or ".git"))
            .Order(StringComparer.Ordinal);
    }
}
