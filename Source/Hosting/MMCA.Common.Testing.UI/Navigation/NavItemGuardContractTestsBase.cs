using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using Xunit;

namespace MMCA.Common.Testing.UI.Navigation;

/// <summary>
/// Menu-to-page guard contract (rubric section 25): every sidebar entry an application UI module
/// contributes (<see cref="IUIModule.NavItems"/>) must be no looser than the guard of the page it
/// opens, so the menu never offers a link the page will refuse. The shared NavMenu shows an item to a
/// caller who satisfies its <see cref="NavItem.RequiredRole"/>, <see cref="NavItem.RequiredClaim"/>
/// and <see cref="NavItem.RequiredPermission"/> (all three, each only when set), and shows the User
/// and Admin sections to signed-in callers only, so an item is treated as anonymous exactly when it
/// sets none of the three and sits in the General section.
/// <para>
/// The rules, per page the item's <see cref="NavItem.Href"/> resolves to: (a) a page whose guard names
/// roles needs an item requiring one of those roles (no permission or claim implies a role
/// statically, because the role-to-permission map is editable at runtime); (b) a page whose guard
/// names a policy needs an item carrying what that policy enforces, read from the policy as the
/// framework registers it; (c) any guarded page needs a non-anonymous item. Pages are the
/// application's own routable components, read from source by
/// <see cref="NavigationContractTestsBase.DiscoverPageRoutes"/> on <see cref="NavigationContract"/>
/// (so a MAUI-only page counts), plus the framework's routable components in MMCA.Common.UI, read by
/// reflection with their policies taken from <c>AddUIShared</c>.
/// </para>
/// <para>
/// Nav items come from each module instance when its assembly is next to the test assembly, and from
/// the module's source otherwise (a MAUI head a plain test project cannot reference); an entry the
/// source reader cannot resolve fails the gate rather than being skipped. The source reader resolves
/// string literals, <see langword="null"/>, <c>NavSection.*</c> and the constants of
/// <see cref="RoleNamesType"/>.
/// </para>
/// </summary>
public abstract partial class NavItemGuardContractTestsBase
{
    private const string NotificationPageRequirementTypeName = "NotificationPageRequirement";

    private const string PagePermissionRequirementTypeName = "PagePermissionRequirement";

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly char[] QueryOrFragment = ['?', '#'];

    private static readonly string[] PositionalRequirementNames =
        [nameof(NavItem.RequiredRole), nameof(NavItem.RequiredClaim), nameof(NavItem.RequiredPermission), nameof(NavItem.Section), nameof(NavItem.Group)];

    /// <summary>The repository root, normally <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>
    /// The application's navigation contract (its <see cref="NavigationContractTestsBase"/>
    /// subclass), whose discovered pages and access words this gate compares the menu against.
    /// </summary>
    protected abstract NavigationContractTestsBase NavigationContract { get; }

    /// <summary>
    /// The application's role-name constants class (for example <c>typeof(RoleNames)</c>), used to
    /// resolve <c>RoleNames.X</c> in a module read from source.
    /// </summary>
    protected abstract Type RoleNamesType { get; }

    /// <summary>The repo-relative folder holding the UI modules.</summary>
    protected virtual string SourceRoot => "Source";

    [Fact]
    public void NavItems_AreReadFromEveryUIModule_GateIsNotVacuous()
    {
        var modules = DiscoverModules();
        var problems = new List<string>();
        if (modules.Count == 0)
        {
            problems.Add($"no IUIModule implementation was found under {SourceRoot}/");
        }

        problems.AddRange(modules.SelectMany(module => module.Problems));
        if (modules.Sum(module => module.Items.Count) == 0)
        {
            problems.Add($"zero nav items read from {modules.Count.ToString(CultureInfo.InvariantCulture)} module(s)");
        }

        string.Join(Environment.NewLine, problems).Should().BeEmpty(
            because: $"the menu-to-page guard gate must read every nav item of every UI module, or it passes vacuously: reference the module's assembly from the test project, or keep a source-read module's entries in the literal form the reader understands (string-literal Href, {RoleNamesType.Name}.* / string-literal requirements) (rubric section 25)");
    }

    [Fact]
    public void EveryNavItem_OpensARoutablePage()
    {
        var pages = DiscoverGuardedPages();
        var violations = DiscoverModules()
            .SelectMany(module => module.Items.Select(item => (module, item)))
            .Where(entry => !pages.Exists(page => RouteMatches(page.Route, entry.item.Href)))
            .Select(entry => $"{entry.module.Name} {entry.item.Title} -> {entry.item.Href}: no routable page declares a matching @page route")
            .ToList();

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: "every nav item must open a page whose guard the contract can compare against; a dead link is a defect on its own, and an unmatched item would make the guard comparison pass without checking anything (rubric section 25)");
    }

    [Fact]
    public void EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens()
    {
        var pages = DiscoverGuardedPages();
        var violations = new List<string>();
        foreach (var module in DiscoverModules())
        {
            foreach (var item in module.Items)
            {
                foreach (var page in pages.Where(page => RouteMatches(page.Route, item.Href)))
                {
                    violations.AddRange(Compare(item, page).Select(problem => $"{module.Name} {item.Title} -> {item.Href} ({page.Source}, {page.Route}): {problem}"));
                }
            }
        }

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            because: "a nav item must require at least what its page's guard enforces, or the menu offers signed-out or under-privileged callers a link that ends in a sign-in challenge or Access Denied; fix the NAV ITEM (add the RequiredRole, RequiredClaim or RequiredPermission the page enforces, or move it to the User/Admin section), never loosen the page guard (rubric section 25)");
    }

    // One problem per rule the item fails, in rule order: authentication, role, claim, permission.
    private static IEnumerable<string> Compare(NavItem item, GuardedPage page)
    {
        if (page.Guard is not { } guard)
        {
            return [$"the page guard could not be read ({page.Problem})"];
        }

        string?[] problems =
        [
            CompareAuthentication(item, guard),
            CompareRoles(item, guard),
            CompareClaim(item, guard),
            ComparePermission(item, guard),
        ];
        return problems.OfType<string>();
    }

    private static string? CompareAuthentication(NavItem item, PageGuard guard) =>
        guard.RequiresAuthentication && IsAnonymous(item)
            ? $"the page requires a signed-in caller ({guard.Description}) but the item is shown to anonymous visitors"
            : null;

    // The shared NavMenu shows an item with no requirement in the General section to everyone.
    private static bool IsAnonymous(NavItem item) =>
        item.RequiredRole is null && item.RequiredClaim is null && item.RequiredPermission is null && item.Section is NavSection.General;

    private static string? CompareRoles(NavItem item, PageGuard guard) =>
        guard.AnyOfRoles.Count > 0 && !RequiresOneOfTheRoles(item, guard)
            ? $"the page requires one of the roles [{string.Join(", ", guard.AnyOfRoles)}] but the item requires role '{item.RequiredRole ?? "<none>"}'"
            : null;

    private static bool RequiresOneOfTheRoles(NavItem item, PageGuard guard) =>
        item.RequiredRole is not null && guard.AnyOfRoles.Contains(item.RequiredRole, StringComparer.Ordinal);

    private static string? CompareClaim(NavItem item, PageGuard guard) =>
        guard.ClaimType is not null && !string.Equals(item.RequiredClaim, guard.ClaimType, StringComparison.Ordinal)
            ? $"the page requires the claim '{guard.ClaimType}' but the item requires claim '{item.RequiredClaim ?? "<none>"}'"
            : null;

    private static string? ComparePermission(NavItem item, PageGuard guard) =>
        guard.Permission is not null && !string.Equals(item.RequiredPermission, guard.Permission, StringComparison.Ordinal)
            ? $"the page requires the permission '{guard.Permission}' but the item requires permission '{item.RequiredPermission ?? "<none>"}'"
            : null;

    private static string? PositionalName(int position) =>
        position < PositionalRequirementNames.Length ? PositionalRequirementNames[position] : null;

    private static List<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (character == '"' && (index == 0 || arguments[index - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && character is '(' or '[')
            {
                depth++;
            }
            else if (!inString && character is ')' or ']')
            {
                depth--;
            }
            else if (!inString && depth == 0 && character == ',')
            {
                parts.Add(arguments[start..index]);
                start = index + 1;
            }
        }

        parts.Add(arguments[start..]);
        return [.. parts.Where(part => part.Trim().Length > 0)];
    }

    private static AuthorizationOptions ReadFrameworkAuthorizationOptions()
    {
        // The framework registers the policies its own pages reference inside AddUIShared, which every
        // UI host calls; reading them from that registration keeps this gate on the real policy rather
        // than on a restatement of it.
        var services = new ServiceCollection();
        services.AddUIShared(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
    }

    private static (PageGuard? Guard, string? Problem) ReadComponentGuard(Type component, AuthorizationOptions policies)
    {
        if (component.IsDefined(typeof(AllowAnonymousAttribute), inherit: true))
        {
            return (new PageGuard(false, [], null, null, "[AllowAnonymous]"), null);
        }

        var authorize = component.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        if (authorize.Count == 0)
        {
            return (new PageGuard(false, [], null, null, "no [Authorize]"), null);
        }

        var guard = new GuardBuilder(ReadAttributeRoles(authorize));
        foreach (var policyName in authorize.Select(attribute => attribute.Policy).OfType<string>())
        {
            if (ReadPolicy(policyName, policies, guard) is { } problem)
            {
                return (null, problem);
            }
        }

        var description = string.Join(" ", authorize.Select(attribute => $"[Authorize(Roles = {attribute.Roles ?? "-"}, Policy = {attribute.Policy ?? "-"})]"));
        return (new PageGuard(true, guard.Roles, guard.ClaimType, guard.Permission, description), null);
    }

    private static List<string> ReadAttributeRoles(List<AuthorizeAttribute> authorize)
    {
        var roles = authorize
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
            .SelectMany(attribute => attribute.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        return roles;
    }

    // Folds what one named policy enforces into the guard; returns the problem when it cannot.
    private static string? ReadPolicy(string policyName, AuthorizationOptions policies, GuardBuilder guard)
    {
        if (policies.GetPolicy(policyName) is not { } policy)
        {
            return $"policy '{policyName}' is not registered by AddUIShared";
        }

        foreach (var requirement in policy.Requirements)
        {
            if (!ApplyRequirement(requirement, guard))
            {
                return $"policy '{policyName}' carries requirement {requirement.GetType().Name}, which this gate does not know how to compare; teach NavItemGuardContractTestsBase what it enforces";
            }
        }

        return null;
    }

    // False when the requirement is not one this gate knows how to compare.
    private static bool ApplyRequirement(IAuthorizationRequirement requirement, GuardBuilder guard)
    {
        switch (requirement)
        {
            case DenyAnonymousAuthorizationRequirement:
                return true;
            case RolesAuthorizationRequirement rolesRequirement:
                guard.Roles.AddRange(rolesRequirement.AllowedRoles);
                return true;
            case ClaimsAuthorizationRequirement claimsRequirement:
                ApplyClaimsRequirement(claimsRequirement, guard);
                return true;
            default:
                return ApplyFrameworkRequirement(requirement, guard);
        }
    }

    // A permission claim with exactly one allowed value is that permission; any other claim is a claim type.
    private static void ApplyClaimsRequirement(ClaimsAuthorizationRequirement requirement, GuardBuilder guard)
    {
        if (string.Equals(requirement.ClaimType, AuthClaimTypes.Permission, StringComparison.Ordinal)
            && requirement.AllowedValues is { } values
            && values.Take(2).Count() == 1)
        {
            guard.Permission = values.Single();
        }
        else
        {
            guard.ClaimType = requirement.ClaimType;
        }
    }

    // The framework's own requirements, matched by name because their types are internal to MMCA.Common.UI.
    private static bool ApplyFrameworkRequirement(IAuthorizationRequirement requirement, GuardBuilder guard)
    {
        var name = requirement.GetType().Name;
        if (string.Equals(name, PagePermissionRequirementTypeName, StringComparison.Ordinal))
        {
            // [HasPagePermission(X)]: the caller's token carries the permission claim X
            // (PagePermissionRequirement's own contract).
            guard.Permission = ReadPermissionProperty(requirement);
            return true;
        }

        if (string.Equals(name, NotificationPageRequirementTypeName, StringComparison.Ordinal))
        {
            // [Authorize] semantics plus its Permission when set (NotificationPageRequirement's
            // own contract, evaluated by NotificationPageAuthorizationHandler).
            guard.Permission ??= ReadPermissionProperty(requirement);
            return true;
        }

        return false;
    }

    private static string? ReadPermissionProperty(IAuthorizationRequirement requirement) =>
        requirement.GetType().GetProperty("Permission")?.GetValue(requirement) as string;

    private static bool RouteMatches(string template, string href)
    {
        var path = href.Split(QueryOrFragment)[0];
        var hrefSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var templateSegments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < templateSegments.Length; index++)
        {
            var segment = templateSegments[index];
            var isParameter = segment is ['{', .., '}'];
            if (isParameter && segment.StartsWith("{*", StringComparison.Ordinal))
            {
                return true;
            }

            if (index >= hrefSegments.Length)
            {
                return isParameter && segment.EndsWith("?}", StringComparison.Ordinal) && templateSegments.Skip(index).All(rest => rest.EndsWith("?}", StringComparison.Ordinal));
            }

            if (!isParameter && !string.Equals(segment, hrefSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Honor the route constraints the way the router does: /sessions/selection-dashboard is
            // not /sessions/{Id:int}, because "selection-dashboard" is not an int.
            if (isParameter && !SatisfiesConstraint(segment, hrefSegments[index]))
            {
                return false;
            }
        }

        return hrefSegments.Length == templateSegments.Length;
    }

    // The int and guid constraints are honored; any other (or no) constraint matches any segment.
    private static bool SatisfiesConstraint(string templateSegment, string value)
    {
        var constraint = templateSegment.Trim('{', '}', '?').Split(':').Skip(1).FirstOrDefault();
        return constraint switch
        {
            "int" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "guid" => Guid.TryParse(value, out _),
            _ => true,
        };
    }

    private static bool IsSourceFile(string repoRoot, string path)
    {
        var segments = Path.GetRelativePath(repoRoot, path).Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindOwningAssemblyName(string repoRoot, string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null && directory.Length >= repoRoot.Length; directory = Path.GetDirectoryName(directory))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
            if (project is not null)
            {
                return Path.GetFileNameWithoutExtension(project);
            }
        }

        return null;
    }

    // ---- Nav items ------------------------------------------------------------------------------
    private List<UIModuleNavItems> DiscoverModules()
    {
        var repoRoot = RepoRoot;
        var sourceRoot = Path.Combine(repoRoot, SourceRoot);
        var modules = new List<UIModuleNavItems>();
        if (!Directory.Exists(sourceRoot))
        {
            return modules;
        }

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).Where(path => IsSourceFile(repoRoot, path)))
        {
            var text = File.ReadAllText(path);
            foreach (Match declaration in ModuleDeclarationRegex.Matches(text))
            {
                var name = declaration.Groups["name"].Value;
                var relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                modules.Add(ReadModule(name, relativePath, FindOwningAssemblyName(repoRoot, path), text));
            }
        }

        return modules;
    }

    private UIModuleNavItems ReadModule(string name, string relativePath, string? assemblyName, string text)
    {
        if (assemblyName is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")))
        {
            var type = Assembly.Load(assemblyName).GetTypes()
                .SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal) && typeof(IUIModule).IsAssignableFrom(candidate));
            return type is null
                ? new UIModuleNavItems(name, relativePath, [], [$"{name} ({relativePath}): assembly {assemblyName} is referenced but declares no IUIModule named {name}"])
                : new UIModuleNavItems(name, relativePath, ((IUIModule)Activator.CreateInstance(type)!).NavItems, []);
        }

        return ReadModuleFromSource(name, relativePath, text);
    }

    private UIModuleNavItems ReadModuleFromSource(string name, string relativePath, string text)
    {
        var block = NavItemsBlockRegex.Match(text);
        if (!block.Success)
        {
            return new UIModuleNavItems(name, relativePath, [], [$"{name} ({relativePath}): no 'NavItems {{ get; }} = [ ... ];' initializer found to read"]);
        }

        var items = new List<NavItem>();
        var problems = new List<string>();
        foreach (Match entry in NavItemEntryRegex.Matches(block.Groups["body"].Value))
        {
            var (item, problem) = ParseEntry(SplitArguments(entry.Groups["args"].Value));
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                problems.Add($"{name} ({relativePath}): cannot read nav item 'new({entry.Groups["args"].Value.Trim()})' from source: {problem}");
            }
        }

        return new UIModuleNavItems(name, relativePath, items, problems);
    }

    private (NavItem? Item, string? Problem) ParseEntry(List<string> arguments)
    {
        if (arguments.Count < 4 || StringLiteralRegex.Match(arguments[0]) is not { Success: true } title || StringLiteralRegex.Match(arguments[1]) is not { Success: true } href)
        {
            return (null, "expected a string-literal Title and Href as the first two arguments");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 4; index < arguments.Count; index++)
        {
            var named = NamedArgumentRegex.Match(arguments[index]);
            var key = named.Success ? named.Groups["name"].Value : PositionalName(index - 4);
            var expression = named.Success ? named.Groups["value"].Value.Trim() : arguments[index].Trim();
            if (key is null || !TryResolveValue(expression, out var value))
            {
                return (null, $"argument '{arguments[index].Trim()}' is not a string literal, null, {RoleNamesType.Name}.* or NavSection.*");
            }

            values[key] = value;
        }

        var section = values.TryGetValue(nameof(NavItem.Section), out var sectionName) && sectionName is not null
            ? Enum.Parse<NavSection>(sectionName)
            : NavSection.General;
        return (new NavItem(
            title.Groups["value"].Value,
            href.Groups["value"].Value,
            Icon: string.Empty,
            TitleResource: typeof(NavItemGuardContractTestsBase),
            RequiredRole: values.GetValueOrDefault(nameof(NavItem.RequiredRole)),
            RequiredClaim: values.GetValueOrDefault(nameof(NavItem.RequiredClaim)),
            RequiredPermission: values.GetValueOrDefault(nameof(NavItem.RequiredPermission)),
            Section: section,
            Group: values.GetValueOrDefault(nameof(NavItem.Group))), null);
    }

    private bool TryResolveValue(string expression, out string? value)
    {
        value = null;
        if (string.Equals(expression, "null", StringComparison.Ordinal))
        {
            return true;
        }

        if (StringLiteralRegex.Match(expression) is { Success: true } literal)
        {
            value = literal.Groups["value"].Value;
            return true;
        }

        if (MemberAccessRegex.Match(expression) is not { Success: true } member)
        {
            return false;
        }

        var typeName = member.Groups["type"].Value;
        var memberName = member.Groups["member"].Value;
        if (string.Equals(typeName, RoleNamesType.Name, StringComparison.Ordinal))
        {
            value = RoleNamesType.GetField(memberName, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
                ? field.GetRawConstantValue() as string
                : null;
            return value is not null;
        }

        if (string.Equals(typeName, nameof(NavSection), StringComparison.Ordinal))
        {
            value = Enum.TryParse<NavSection>(memberName, out _) ? memberName : null;
            return value is not null;
        }

        return false;
    }

    // ---- Pages ----------------------------------------------------------------------------------
    private List<GuardedPage> DiscoverGuardedPages()
    {
        var pages = NavigationContract.DiscoverPageRoutes().Select(ToGuardedPage).ToList();

        var policies = ReadFrameworkAuthorizationOptions();
        foreach (var component in typeof(NavItem).Assembly.GetTypes().Where(type => type.IsDefined(typeof(RouteAttribute), inherit: false)))
        {
            var (guard, problem) = ReadComponentGuard(component, policies);
            pages.AddRange(component.GetCustomAttributes<RouteAttribute>(inherit: false)
                .Select(route => new GuardedPage(route.Template, component.FullName!, guard, problem)));
        }

        return pages;
    }

    // An application page's access word, as NavigationContractTestsBase classified it, back into the
    // guard it stands for: a role word is the role value the page requires.
    private static GuardedPage ToGuardedPage(NavigationPage page) => page.Access switch
    {
        null => new GuardedPage(page.Route, page.RelativePath, null, page.Violation ?? "unrecognized guard"),
        NavigationContractTestsBase.AnonymousAccess
            => new GuardedPage(page.Route, page.RelativePath, new PageGuard(false, [], null, null, "[AllowAnonymous]"), null),
        NavigationContractTestsBase.AuthenticatedAccess
            => new GuardedPage(page.Route, page.RelativePath, new PageGuard(true, [], null, null, "[Authorize]"), null),
        { } access when access.StartsWith(NavigationContractTestsBase.PermissionAccessPrefix, StringComparison.Ordinal)
            => new GuardedPage(page.Route, page.RelativePath, new PageGuard(true, [], null, access[NavigationContractTestsBase.PermissionAccessPrefix.Length..], $"[HasPagePermission({access})]"), null),
        { } role => new GuardedPage(page.Route, page.RelativePath, new PageGuard(true, [role], null, null, $"[Authorize(Roles = {role})]"), null),
    };

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)\s*:\s*(?:[\w.<>]+\s*,\s*)*IUIModule\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ModuleDeclarationRegex { get; }

    [GeneratedRegex(@"\bNavItems\s*\{\s*get;\s*\}\s*=\s*\[(?<body>.*?)\];", RegexOptions.ExplicitCapture | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NavItemsBlockRegex { get; }

    [GeneratedRegex(@"\bnew\s*\((?<args>(?:[^()]|\([^()]*\))*)\)", RegexOptions.ExplicitCapture | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NavItemEntryRegex { get; }

    [GeneratedRegex(@"^\s*""(?<value>[^""]*)""\s*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StringLiteralRegex { get; }

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*:(?<value>.*)$", RegexOptions.ExplicitCapture | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedArgumentRegex { get; }

    [GeneratedRegex(@"^(?<type>\w+)\.(?<member>\w+)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MemberAccessRegex { get; }

    private sealed record UIModuleNavItems(string Name, string RelativePath, IReadOnlyList<NavItem> Items, IReadOnlyList<string> Problems);

    private sealed record GuardedPage(string Route, string Source, PageGuard? Guard, string? Problem);

    private sealed record PageGuard(bool RequiresAuthentication, IReadOnlyList<string> AnyOfRoles, string? ClaimType, string? Permission, string Description);

    // What a component's [Authorize] attributes and their policies add up to, while they are read.
    private sealed class GuardBuilder(List<string> roles)
    {
        public List<string> Roles { get; } = roles;

        public string? ClaimType { get; set; }

        public string? Permission { get; set; }
    }
}
