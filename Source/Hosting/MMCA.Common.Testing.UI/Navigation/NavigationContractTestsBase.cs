using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing.UI.Navigation;

/// <summary>
/// Navigation contract drift gate (rubric section 25): the <c>## Route Contract</c> table of an
/// application's published navigation document must stay in lockstep with the routable pages the
/// application ships and the access guard each one declares.
/// <para>
/// Reality is read from SOURCE, not reflection, so every head counts (a MAUI-only page included):
/// every <c>*.razor</c> under <see cref="SourceRoot"/> with an <c>@page</c> directive, its access
/// taken from the page's <c>@attribute [...]</c> line or, when absent, from an attribute on the
/// partial class in the sibling <c>.razor.cs</c>. The table is the requirement: a route, re-route or
/// guard change lands together with its row in the document.
/// </para>
/// <para>
/// The contract has four access forms, one per page: <c>[AllowAnonymous]</c> (Access
/// <see cref="AnonymousAccess"/>), a bare <c>[Authorize]</c> (<see cref="AuthenticatedAccess"/>),
/// <c>[Authorize(Roles = RoleNames.X)]</c> (the role's value, which must be one of
/// <see cref="PageRoles"/>), and, only when <see cref="PermissionConstantsType"/> is set,
/// <c>[HasPagePermission(Permissions.X)]</c> (<see cref="PermissionAccessPrefix"/> plus the
/// permission's value).
/// </para>
/// </summary>
/// <remarks>
/// The document normally lives in another repo. CI downloads the published copy and points
/// <see cref="DocEnvironmentVariable"/> at it; a local run falls back to <see cref="DefaultDocPath"/>,
/// the sibling Website checkout. A missing document FAILS the gate rather than skipping it, so the
/// contract can never pass unread. <see cref="DiscoverPageRoutes"/> is public so
/// <see cref="NavItemGuardContractTestsBase"/> compares menu entries against the same page list.
/// </remarks>
public abstract partial class NavigationContractTestsBase
{
    /// <summary>The Access word for a page guarded by <c>[AllowAnonymous]</c>.</summary>
    public const string AnonymousAccess = "Anonymous";

    /// <summary>The Access word for a page guarded by a bare <c>[Authorize]</c>.</summary>
    public const string AuthenticatedAccess = "Authenticated";

    /// <summary>The Access prefix for a page guarded by <c>[HasPagePermission(Permissions.X)]</c>.</summary>
    public const string PermissionAccessPrefix = "Permission: ";

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The navigation document's file name, for example <c>adc-NavigationFlow.md</c>.</summary>
    protected abstract string DocFileName { get; }

    /// <summary>
    /// The environment variable that, when set, holds the document's path (CI points it at the
    /// published copy), for example <c>ADC_NAVIGATION_DOC</c>.
    /// </summary>
    protected abstract string DocEnvironmentVariable { get; }

    /// <summary>
    /// The fewest routable pages the scan must find; set it a little under today's count so a moved
    /// tree or a changed directive shape cannot make the gate pass vacuously.
    /// </summary>
    protected abstract int MinimumRoutes { get; }

    /// <summary>
    /// The application's role-name constants class (for example <c>typeof(RoleNames)</c>), whose
    /// <c>public const string</c> members resolve a <c>[Authorize(Roles = RoleNames.X)]</c> guard.
    /// </summary>
    protected abstract Type RoleNamesType { get; }

    /// <summary>
    /// The role values a page may require, each also an Access word. Defaults to every
    /// <c>public const string</c> on <see cref="RoleNamesType"/>; override to narrow it (ADC allows
    /// only <c>Organizer</c>).
    /// </summary>
    protected virtual IReadOnlyCollection<string> PageRoles => ReadConstants(RoleNamesType);

    /// <summary>
    /// The application's permission constants class (for example <c>typeof(ConferencePermissions)</c>)
    /// that <c>[HasPagePermission(...)]</c> guards name. <see langword="null"/> by default, which
    /// switches the permission form off: a page guarded that way is then not a contract form.
    /// </summary>
    protected virtual Type? PermissionConstantsType => null;

    /// <summary>The repo-relative folder holding the <c>.razor</c> pages.</summary>
    protected virtual string SourceRoot => "Source";

    /// <summary>
    /// Where the document is read from when <see cref="DocEnvironmentVariable"/> is unset: the
    /// sibling Website checkout, <c>../Website/docs-src/guides/&lt;DocFileName&gt;</c>.
    /// </summary>
    protected virtual string DefaultDocPath =>
        Path.GetFullPath(Path.Combine(RepoRoot, "..", "Website", "docs-src", "guides", DocFileName));

    [Fact]
    public void RoutablePages_AreDiscovered_GateIsNotVacuous() =>
        DiscoverPageRoutes().Should().HaveCountGreaterThanOrEqualTo(
            MinimumRoutes,
            because: $"the application ships at least {MinimumRoutes} routable pages; discovering fewer means the @page scan under {SourceRoot}/ drifted (a moved tree, a changed directive shape) and the navigation drift gate would pass vacuously");

    [Fact]
    public void EveryRoutablePage_DeclaresARecognizedAccessAttribute()
    {
        var pages = DiscoverPageRoutes();
        var violations = pages
            .Where(page => page.Violation is not null)
            .Select(page => $"{page.Route}: {page.RelativePath} {page.Violation}")
            .ToList();

        foreach (var group in pages.GroupBy(page => page.Route, StringComparer.Ordinal).Where(group => group.Skip(1).Any()))
        {
            violations.Add($"{group.Key}: declared by more than one page ({string.Join(", ", group.Select(page => page.RelativePath))})");
        }

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: $"every routable page must state its guard as exactly one of {DescribeForms()}, on its @attribute line or on the partial class in its .razor.cs, so the Route Contract's Access column ({DescribeAccessWords()}) can be verified; add the missing attribute, or move the page to one of those forms (and its contract row with it) (rubric section 25)");
    }

    [Fact]
    public void EveryRoutablePage_IsInTheRouteContract_AndEveryContractRoute_Exists()
    {
        var code = DiscoverPageRoutes().Select(page => page.Route).ToHashSet(StringComparer.Ordinal);
        var documented = ReadContractTable().Rows.Select(row => row.Route).ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        violations.AddRange(code.Except(documented, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(route => $"undocumented: {route} has an @page but no Route Contract row"));
        violations.AddRange(documented.Except(code, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(route => $"phantom: {route} has a Route Contract row but no page declares it"));

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: $"the Route Contract table in {DocFileName} ({DescribeDocLocation()}) must list exactly the @page routes the application ships, compared verbatim (route constraints such as {{Id:int}} included): add a row for each undocumented route and remove or re-route each phantom row, landing the document change before or with the code change (rubric section 25)");
    }

    [Fact]
    public void EveryContractAccess_MatchesThePageGuard()
    {
        var pages = DiscoverPageRoutes()
            .Where(page => page.Access is not null)
            .GroupBy(page => page.Route, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var row in ReadContractTable().Rows)
        {
            // Unknown routes are the set-equality fact's to report, an unrecognized guard the
            // attribute fact's, and an invalid Access word the table-hygiene fact's.
            if (!IsValidAccess(row.Access) || !pages.TryGetValue(row.Route, out var page))
            {
                continue;
            }

            if (!string.Equals(row.Access, page.Access, StringComparison.Ordinal))
            {
                violations.Add($"{row.Route}: doc says {row.Access}, page {page.RelativePath} declares {page.Access}");
            }
        }

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: $"the Route Contract's Access column is the route-guard requirement: each page's guard ({DescribeForms()}) must say what its row says; when the guard change is intended, update the row in {DocFileName} first, otherwise restore the page's guard (rubric section 25)");
    }

    [Fact]
    public void RouteContractTable_IsWellFormed()
    {
        var (rows, tableProblems) = ReadContractTable();
        var violations = new List<string>(tableProblems);

        foreach (var group in rows.GroupBy(row => row.Route, StringComparer.Ordinal).Where(group => group.Skip(1).Any()))
        {
            violations.Add($"{group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)} rows; keep exactly one row per route");
        }

        foreach (var row in rows.Where(row => !IsValidAccess(row.Access)))
        {
            violations.Add($"{row.Route}: Access '{row.Access}' is not one of {DescribeAccessWords()}");
        }

        var pagesByRoute = DiscoverPageRoutes()
            .GroupBy(page => page.Route, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var row in rows.Where(row => !string.Equals(row.Page, "-", StringComparison.Ordinal)))
        {
            if (pagesByRoute.TryGetValue(row.Route, out var page) && !string.Equals(row.Page, page.PageName, StringComparison.Ordinal))
            {
                violations.Add($"{row.Route}: Page '{row.Page}' does not match the page that declares it ({page.PageName}, {page.RelativePath}); write the .razor file name without extension, or '-'");
            }
        }

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: $"{DocFileName} ({DescribeDocLocation()}) must carry a '## Route Contract' section whose '| Route | Page | Access |' table has one row per route, a backticked route template, an Access of exactly {DescribeAccessWords()}, and a Page cell that is '-' or the declaring .razor file's name; fix the listed rows in the document (rubric section 25)");
    }

    /// <summary>
    /// Every routable page under <see cref="SourceRoot"/>, read from source, with the access word its
    /// guard maps to. <see cref="NavItemGuardContractTestsBase"/> reads the same list.
    /// </summary>
    /// <returns>One entry per <c>@page</c> directive.</returns>
    public IReadOnlyList<NavigationPage> DiscoverPageRoutes()
    {
        var repoRoot = RepoRoot;
        var sourceRoot = Path.Combine(repoRoot, SourceRoot);
        var pages = new List<NavigationPage>();
        if (!Directory.Exists(sourceRoot))
        {
            return pages;
        }

        foreach (var razorPath in Directory.EnumerateFiles(sourceRoot, "*.razor", SearchOption.AllDirectories).Where(path => IsSourceFile(repoRoot, path)))
        {
            var lines = File.ReadAllLines(razorPath);
            var routes = lines
                .Select(line => PageDirectiveRegex.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["route"].Value)
                .ToList();
            if (routes.Count == 0)
            {
                continue;
            }

            var relativeFile = Path.GetRelativePath(repoRoot, razorPath).Replace('\\', '/');
            var pageName = Path.GetFileNameWithoutExtension(razorPath);
            var attributes = lines
                .Select(line => AttributeDirectiveRegex.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["attribute"].Value.Trim())
                .Where(IsAccessAttribute)
                .ToList();
            if (attributes.Count == 0)
            {
                attributes = ReadCodeBehindAccessAttributes(razorPath + ".cs", pageName);
            }

            var (access, violation) = Classify(attributes);
            pages.AddRange(routes.Select(route => new NavigationPage(route, relativeFile, pageName, access, violation)));
        }

        return pages;
    }

    /// <summary>Reads every <c>public const string</c> declared on <paramref name="type"/>.</summary>
    /// <param name="type">The constants class.</param>
    /// <returns>The constant values, in declaration order.</returns>
    protected static IReadOnlyCollection<string> ReadConstants(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];
    }

    private static string? ReadConstant(Type type, string memberName) =>
        type.GetField(memberName, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
            ? field.GetRawConstantValue() as string
            : null;

    private static bool IsSourceFile(string repoRoot, string path)
    {
        var segments = Path.GetRelativePath(repoRoot, path).Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAccessAttribute(string attribute) => AccessAttributeNameRegex.IsMatch(attribute);

    // Attribute lines directly above the page's partial class declaration in its code-behind.
    private static List<string> ReadCodeBehindAccessAttributes(string codeBehindPath, string pageName)
    {
        if (!File.Exists(codeBehindPath))
        {
            return [];
        }

        var lines = File.ReadAllLines(codeBehindPath);
        var classLine = Array.FindIndex(lines, line => PartialClassRegex.Match(line) is { Success: true } match
            && string.Equals(match.Groups["name"].Value, pageName, StringComparison.Ordinal));
        var attributes = new List<string>();
        for (var index = classLine - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line is ['[', ..])
            {
                attributes.Add(line);
            }
            else if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }
        }

        return [.. attributes.Where(IsAccessAttribute)];
    }

    private static bool NamesType(string typeExpression, Type type) =>
        string.Equals(typeExpression.Split('.')[^1], type.Name, StringComparison.Ordinal);

    private (string? Access, string? Violation) Classify(List<string> attributes)
    {
        if (attributes.Count == 0)
        {
            return (null, "declares no access attribute (neither an @attribute line in the .razor nor an attribute on the partial class in the .razor.cs)");
        }

        if (attributes.Count > 1)
        {
            return (null, $"declares several access attributes ({string.Join(" ", attributes)}); keep exactly one");
        }

        var match = AccessAttributeRegex.Match(attributes[0]);
        if (match.Success)
        {
            var arguments = match.Groups["args"].Value.Trim();
            var name = match.Groups["name"].Value;
            if (string.Equals(name, "AllowAnonymous", StringComparison.Ordinal) && arguments.Length == 0)
            {
                return (AnonymousAccess, null);
            }

            if (string.Equals(name, "Authorize", StringComparison.Ordinal) && arguments.Length == 0)
            {
                return (AuthenticatedAccess, null);
            }

            if (string.Equals(name, "Authorize", StringComparison.Ordinal) && ResolveRole(arguments) is { } role)
            {
                return (role, null);
            }

            if (string.Equals(name, "HasPagePermission", StringComparison.Ordinal) && ResolvePermission(arguments) is { } permission)
            {
                return (PermissionAccessPrefix + permission, null);
            }
        }

        return (null, $"declares {attributes[0]}, which is not one of the contract forms ({DescribeForms()})");
    }

    // "Roles = RoleNames.X" or "Roles = \"X\"", resolved to a value that is one of the page roles.
    private string? ResolveRole(string arguments)
    {
        var match = RolesArgumentRegex.Match(arguments);
        if (!match.Success)
        {
            return null;
        }

        string? value = null;
        if (match.Groups["literal"].Success)
        {
            value = match.Groups["literal"].Value;
        }
        else if (NamesType(match.Groups["type"].Value, RoleNamesType))
        {
            value = ReadConstant(RoleNamesType, match.Groups["member"].Value);
        }

        return value is not null && PageRoles.Contains(value, StringComparer.Ordinal) ? value : null;
    }

    // "Permissions.X", resolved through PermissionConstantsType; off when that type is not set.
    private string? ResolvePermission(string arguments)
    {
        var permissionType = PermissionConstantsType;
        if (permissionType is null || ConstantArgumentRegex.Match(arguments) is not { Success: true } match || !NamesType(match.Groups["type"].Value, permissionType))
        {
            return null;
        }

        return ReadConstant(permissionType, match.Groups["member"].Value);
    }

    // An Access cell is Anonymous, Authenticated, one of the page roles, or "Permission: " followed by
    // a real permission value when the permission form is on.
    private bool IsValidAccess(string access) =>
        string.Equals(access, AnonymousAccess, StringComparison.Ordinal)
        || string.Equals(access, AuthenticatedAccess, StringComparison.Ordinal)
        || PageRoles.Contains(access, StringComparer.Ordinal)
        || PermissionConstantsType is { } permissionType
            && access.StartsWith(PermissionAccessPrefix, StringComparison.Ordinal)
            && ReadConstants(permissionType).Contains(access[PermissionAccessPrefix.Length..], StringComparer.Ordinal);

    private string DescribeForms()
    {
        var forms = $"[AllowAnonymous], [Authorize], [Authorize(Roles = {RoleNamesType.Name}.X)] for a role in [{string.Join(", ", PageRoles)}]";
        return PermissionConstantsType is { } permissionType
            ? $"{forms} or [HasPagePermission({permissionType.Name}.X)]"
            : forms;
    }

    private string DescribeAccessWords()
    {
        var words = $"{AnonymousAccess}, {AuthenticatedAccess}, {string.Join(", ", PageRoles)}";
        return PermissionConstantsType is { } permissionType
            ? $"{words} or {PermissionAccessPrefix}<a {permissionType.Name} value>"
            : words;
    }

    private ContractTable ReadContractTable()
    {
        var docPath = ResolveDocPath();
        File.Exists(docPath).Should().BeTrue(
            because: $"the navigation gate reads the Route Contract from {DocFileName}, looked up via the {DocEnvironmentVariable} environment variable and otherwise at the default path {DefaultDocPath}, and neither exists; clone the Website repo next to this repo or set {DocEnvironmentVariable} to the document's path (CI downloads the published copy and sets it)");

        var problems = new List<string>();
        var lines = File.ReadAllLines(docPath);
        var headingIndex = Array.FindIndex(lines, line => RouteContractHeadingRegex.IsMatch(line));
        if (headingIndex < 0)
        {
            problems.Add("missing '## Route Contract' section");
            return new ContractTable([], problems);
        }

        var section = lines.Skip(headingIndex + 1).TakeWhile(line => !SectionHeadingRegex.IsMatch(line)).ToList();
        var tableLines = section
            .SkipWhile(line => line.TrimStart() is not ['|', ..])
            .TakeWhile(line => line.TrimStart() is ['|', ..])
            .ToList();
        if (tableLines.Count < 2 || !HeaderRowRegex.IsMatch(tableLines[0]))
        {
            problems.Add("the '## Route Contract' section has no '| Route | Page | Access |' table");
            return new ContractTable([], problems);
        }

        var rows = new List<ContractRow>();
        foreach (var line in tableLines.Skip(2))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            var route = cells.Length > 0 ? BacktickedRouteRegex.Match(cells[0]) : Match.Empty;
            if (cells.Length != 3 || !route.Success)
            {
                problems.Add($"malformed row '{line.Trim()}': expected three cells with a backticked route template first");
                continue;
            }

            rows.Add(new ContractRow(route.Groups["route"].Value, cells[1], cells[2]));
        }

        if (rows.Count == 0)
        {
            problems.Add("the Route Contract table is empty");
        }

        return new ContractTable(rows, problems);
    }

    private string ResolveDocPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DocEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridePath) ? DefaultDocPath : overridePath;
    }

    private string DescribeDocLocation() => $"read from {ResolveDocPath()}";

    [GeneratedRegex(@"^\s*@page\s+""(?<route>[^""]+)""", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PageDirectiveRegex { get; }

    [GeneratedRegex(@"^\s*@attribute\s+(?<attribute>\[.*\])\s*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeDirectiveRegex { get; }

    [GeneratedRegex(@"^\[\s*((Microsoft\.AspNetCore\.Authorization\.)?(Authorize|AllowAnonymous)|HasPagePermission)(Attribute)?\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AccessAttributeNameRegex { get; }

    [GeneratedRegex(@"^\[\s*(Microsoft\.AspNetCore\.Authorization\.)?(?<name>Authorize|AllowAnonymous|HasPagePermission)(Attribute)?\s*(\((?<args>[^()]*)\))?\s*\]$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AccessAttributeRegex { get; }

    [GeneratedRegex(@"^Roles\s*=\s*((?<type>[\w.]+)\.(?<member>\w+)|""(?<literal>[^""]*)"")$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RolesArgumentRegex { get; }

    [GeneratedRegex(@"^(?<type>[\w.]+)\.(?<member>\w+)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConstantArgumentRegex { get; }

    [GeneratedRegex(@"\bpartial\s+class\s+(?<name>\w+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartialClassRegex { get; }

    [GeneratedRegex(@"^##\s+Route Contract\s*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteContractHeadingRegex { get; }

    [GeneratedRegex(@"^#{1,2}\s", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SectionHeadingRegex { get; }

    [GeneratedRegex(@"^\s*\|\s*Route\s*\|\s*Page\s*\|\s*Access\s*\|\s*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderRowRegex { get; }

    [GeneratedRegex(@"^`(?<route>[^`]+)`$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BacktickedRouteRegex { get; }

    private sealed record ContractRow(string Route, string Page, string Access);

    private sealed record ContractTable(List<ContractRow> Rows, List<string> Problems);
}
