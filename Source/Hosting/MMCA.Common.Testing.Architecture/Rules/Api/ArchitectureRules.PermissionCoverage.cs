namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Matched by full name, and the permission read reflectively, because this package deliberately
    // takes no framework reference (see the csproj note). The attributes live in MMCA.Common.API,
    // MMCA.Common.UI and MMCA.Common.Mcp; AuthorizeAttribute in ASP.NET Core.
    private const string HasPermissionAttributeFullName = "MMCA.Common.API.Authorization.HasPermissionAttribute";
    private const string HasPagePermissionAttributeFullName = "MMCA.Common.UI.Authorization.HasPagePermissionAttribute";
    private const string McpToolPermissionAttributeFullName = "MMCA.Common.Mcp.McpToolPermissionAttribute";
    private const string PlainAuthorizeAttributeFullName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";

    // The policy-name prefixes PermissionPolicy (API) and PagePermissionPolicy (UI) put in front of a
    // permission, so a hand-written [Authorize(Policy = "perm:x")] still names permission x.
    private const string PermissionPolicyPrefix = "perm:";
    private const string PagePermissionPolicyPrefix = "page-perm:";

    private const string AdditionalReferencesShape = "AdditionalReferencedPermissions";

    /// <summary>
    /// Every permission a controller, page, MCP tool or menu entry names is granted to at least one
    /// role. A permission no role holds is a gate nobody can pass: the page, endpoint or tool is
    /// unreachable, and nothing else in a codebase notices until someone is refused at run time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four shapes are read: <c>[HasPermission]</c>, <c>[HasPagePermission]</c>,
    /// <c>[McpToolPermission]</c>, and a plain <c>[Authorize(Policy = "perm:...")]</c> (or
    /// <c>"page-perm:..."</c>). Menu entries are built in code (<c>NavItem.RequiredPermission</c>), not
    /// declared by attribute, so the caller passes them in <paramref name="additionalReferencedPermissions"/>.
    /// </para>
    /// <para>
    /// A type's attributes are read WITH inheritance: an app's controller derived from a framework
    /// base exposes the base's class-level gate, so the app must grant it. A method's attributes are
    /// read only where declared: a framework base action is checked once by the framework's own
    /// subclass, and an app that leaves the capability off is not told to grant it.
    /// </para>
    /// <para>
    /// The comparison is ordinal, as the permission registry's is. Only this direction is checked: a
    /// declared permission nothing names is not an error.
    /// </para>
    /// </remarks>
    /// <param name="assemblies">The assemblies whose types and methods are scanned.</param>
    /// <param name="declaredPermissions">
    /// The permissions granted to some role, typically the <c>IPermissionCatalog.Permissions</c> of the
    /// registry the token issuer builds (it lists granted permissions only).
    /// </param>
    /// <param name="additionalReferencedPermissions">Permissions named outside an attribute, such as menu entries.</param>
    public static void NamedPermissionsAreGranted(
        IEnumerable<Assembly> assemblies,
        IReadOnlyCollection<string> declaredPermissions,
        IEnumerable<string>? additionalReferencedPermissions = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(declaredPermissions);

        var granted = declaredPermissions.ToHashSet(StringComparer.Ordinal);
        var additional = (additionalReferencedPermissions ?? [])
            .Select(permission => new PermissionReference(permission, AdditionalReferencesShape, "the subclass"));

        var violations = PermissionReferences(assemblies)
            .Concat(additional)
            .Where(reference => !granted.Contains(reference.Permission))
            .Select(reference => $"  - '{reference.Permission}' named by {reference.Shape} on {reference.Location}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(violations,
            "every permission a controller, page, MCP tool or menu entry names must be granted to at least one role, "
            + "or nobody can ever pass that gate");
    }

    /// <summary>
    /// Counts the permission references <see cref="NamedPermissionsAreGranted"/> sees in
    /// <paramref name="assemblies"/>: a non-vacuity floor, so a renamed assembly or attribute cannot let
    /// the rule pass without having inspected anything.
    /// </summary>
    /// <param name="assemblies">The assemblies whose types and methods are scanned.</param>
    /// <returns>The number of permission references found.</returns>
    public static int CountNamedPermissions(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return PermissionReferences(assemblies).Count();
    }

    /// <summary>One place a permission is named: the permission, the shape that names it, and where.</summary>
    private sealed record PermissionReference(string Permission, string Shape, string Location);

    private static IEnumerable<PermissionReference> PermissionReferences(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags declaredMethods = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assemblies.SelectMany(assembly => assembly.LoadableTypes))
        {
            var typeName = type.FullName ?? type.Name;

            foreach (var reference in ReferencesIn(type.GetCustomAttributes(inherit: true), typeName))
            {
                yield return reference;
            }

            foreach (var method in type.GetMethods(declaredMethods))
            {
                foreach (var reference in ReferencesIn(method.GetCustomAttributes(inherit: false), $"{typeName}.{method.Name}"))
                {
                    yield return reference;
                }
            }
        }
    }

    private static IEnumerable<PermissionReference> ReferencesIn(object[] attributes, string location)
    {
        foreach (var attribute in attributes)
        {
            var reference = attribute.GetType().FullName switch
            {
                HasPermissionAttributeFullName => Reference(ReadValue(attribute, "Permission"), "[HasPermission]", location),
                HasPagePermissionAttributeFullName => Reference(ReadValue(attribute, "Permission"), "[HasPagePermission]", location),
                McpToolPermissionAttributeFullName => Reference(ReadValue(attribute, "Permission"), "[McpToolPermission]", location),
                PlainAuthorizeAttributeFullName => Reference(PermissionInPolicy(ReadValue(attribute, "Policy")), "[Authorize(Policy)]", location),
                _ => null,
            };

            if (reference is not null)
            {
                yield return reference;
            }
        }
    }

    private static PermissionReference? Reference(string? permission, string shape, string location) =>
        string.IsNullOrEmpty(permission) ? null : new PermissionReference(permission, shape, location);

    /// <summary>The permission inside a permission policy name, or null for any other policy.</summary>
    private static string? PermissionInPolicy(string? policy)
    {
        // The longer prefix first: "page-perm:" does not start with "perm:", but keep the order explicit.
        if (policy?.StartsWith(PagePermissionPolicyPrefix, StringComparison.Ordinal) == true)
        {
            return policy[PagePermissionPolicyPrefix.Length..];
        }

        return policy?.StartsWith(PermissionPolicyPrefix, StringComparison.Ordinal) == true
            ? policy[PermissionPolicyPrefix.Length..]
            : null;
    }
}
