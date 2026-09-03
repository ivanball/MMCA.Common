namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Route-authorization fitness function (rubric §25): every routable Blazor page the subclass declares
/// as governed must keep its role gate (<c>[Authorize(Roles = "...")]</c>), so an admin route cannot
/// silently regress to a bare <c>[Authorize]</c> reachable by any authenticated user. Authored once
/// here and re-run as a thin subclass per module UI assembly: the subclass supplies its
/// <see cref="TargetAssembly"/>, its <see cref="RequiredRole"/> (e.g. <c>"Organizer"</c> or
/// <c>"Admin"</c>), and its <see cref="IsGovernedPage"/> strategy (exact page names, namespace
/// suffixes, or a pinned page-name array), plus a <see cref="MinimumGovernedPages"/> non-vacuity
/// floor. Reflection-based, so it covers every current and future page matching the strategy without
/// enumerating them by hand. Deliberately anonymous pages (public browse surfaces) and bare
/// <c>[Authorize]</c> self-service pages simply must not match <see cref="IsGovernedPage"/>; the
/// protected helpers let subclasses add their own guards for those sets (e.g. "public pages stay
/// anonymous at the page level").
/// <para>
/// Blazor's <c>RouteAttribute</c> and ASP.NET Core's <c>AuthorizeAttribute</c> are detected by
/// full-name reflection over the attribute instances, keeping this package free of ASP.NET
/// references (see the csproj note).
/// </para>
/// </summary>
public abstract class RouteAuthorizationTestsBase
{
    private const string RouteAttributeFullName = "Microsoft.AspNetCore.Components.RouteAttribute";
    private const string AuthorizeAttributeFullName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";

    /// <summary>The module UI assembly whose routable pages are scanned.</summary>
    protected abstract Assembly TargetAssembly { get; }

    /// <summary>The exact role every governed page must require (e.g. <c>"Organizer"</c>, <c>"Admin"</c>).</summary>
    protected abstract string RequiredRole { get; }

    /// <summary>
    /// Whether a routable page belongs to the governed (role-gated) set. Subclasses pick the strategy
    /// that fits their layout: match the page's namespace exactly, match namespace suffixes, or pin
    /// individual page names when a namespace deliberately mixes governed and self-service pages.
    /// </summary>
    /// <param name="pageType">A routable page component type from <see cref="TargetAssembly"/>.</param>
    /// <returns><see langword="true"/> when the page must carry the role gate.</returns>
    protected abstract bool IsGovernedPage(Type pageType);

    /// <summary>
    /// Minimum number of governed pages the scan must discover: a non-vacuity guard, so a moved
    /// namespace or renamed page cannot let the gate pass without checking anything. Override in the
    /// subclass to the repo's known count so a removed governed page is caught.
    /// </summary>
    protected virtual int MinimumGovernedPages => 1;

    [Fact]
    public void GovernedPages_RequireDeclaredRole()
    {
        var offenders = TargetAssembly.LoadableTypes
            .Where(t => IsRoutablePage(t) && IsGovernedPage(t) && !RequiresRole(t, RequiredRole))
            .Select(t => $"{t.FullName} [{Routes(t)}]")
            .ToList();

        offenders.Should().BeEmpty(
            "every governed page must keep [Authorize(Roles = \"{0}\")] (§25) so it cannot regress to a route any authenticated user can reach; regressed: {1}",
            RequiredRole,
            string.Join("; ", offenders));
    }

    [Fact]
    public void GovernedPageSet_IsNotEmpty()
    {
        // Guard the guard: if a refactor moves the governed namespaces or renames the pinned pages,
        // IsGovernedPage would match nothing and the assertion above would pass vacuously.
        var count = TargetAssembly.LoadableTypes.Count(t => IsRoutablePage(t) && IsGovernedPage(t));

        count.Should().BeGreaterThanOrEqualTo(
            MinimumGovernedPages,
            "the governed page set must actually be inspected (expected at least {0} routable governed page(s))",
            MinimumGovernedPages);
    }

    /// <summary>Whether the type is a routable page (carries Blazor's <c>RouteAttribute</c>).</summary>
    protected static bool IsRoutablePage(Type type) => AttributesOf(type, RouteAttributeFullName).Any();

    /// <summary>
    /// Whether the page's <c>AuthorizeAttribute</c> requires exactly <paramref name="role"/> (a bare
    /// <c>[Authorize]</c>, a missing attribute, or a different role all fail).
    /// </summary>
    protected static bool RequiresRole(Type type, string role)
    {
        var authorize = AttributesOf(type, AuthorizeAttributeFullName).FirstOrDefault();
        var roles = authorize?.GetType().GetProperty("Roles")?.GetValue(authorize) as string;
        return string.Equals(roles, role, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the page carries any <c>AuthorizeAttribute</c> at all: lets subclasses assert that a
    /// deliberately-public page set stays anonymous at the page level.
    /// </summary>
    protected static bool HasAuthorizeAttribute(Type type) => AttributesOf(type, AuthorizeAttributeFullName).Any();

    /// <summary>The page's route templates, for offender messages.</summary>
    protected static string Routes(Type type) =>
        string.Join(", ", AttributesOf(type, RouteAttributeFullName)
            .Select(a => a.GetType().GetProperty("Template")?.GetValue(a) as string));

    private static IEnumerable<object> AttributesOf(Type type, string attributeFullName) =>
        type.GetCustomAttributes(inherit: true)
            .Where(a => IsOrDerivesFrom(a.GetType(), attributeFullName));

    private static bool IsOrDerivesFrom(Type attributeType, string fullName)
    {
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
