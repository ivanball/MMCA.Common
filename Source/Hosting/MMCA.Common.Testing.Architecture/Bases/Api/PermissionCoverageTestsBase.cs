namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Permission coverage fitness function: every permission a controller, page, MCP tool or menu entry
/// names is granted to at least one role, so no surface ships behind a gate nobody can pass. Authored
/// once here and re-run as a thin subclass per repo: the subclass supplies the
/// <see cref="TargetAssemblies"/> to scan, the <see cref="DeclaredPermissions"/> its roles are granted,
/// and the <see cref="AdditionalReferencedPermissions"/> its menu names in code.
/// <para>
/// The shapes read and the inheritance rules are documented on
/// <see cref="ArchitectureRules.NamedPermissionsAreGranted"/>. Only the forward direction is checked:
/// a granted permission nothing names is not an error.
/// </para>
/// </summary>
public abstract class PermissionCoverageTestsBase
{
    /// <summary>The assemblies whose controllers, pages and MCP tools are scanned (API and UI, typically).</summary>
    protected abstract IReadOnlyCollection<Assembly> TargetAssemblies { get; }

    /// <summary>
    /// The permissions granted to at least one role: pass <c>IPermissionCatalog.Permissions</c> of the
    /// same registry the token issuer builds, which lists granted permissions only.
    /// </summary>
    protected abstract IReadOnlyCollection<string> DeclaredPermissions { get; }

    /// <summary>
    /// Permissions named outside an attribute, such as <c>NavItem.RequiredPermission</c> values, which
    /// are built in code and invisible to the attribute scan.
    /// </summary>
    protected virtual IEnumerable<string> AdditionalReferencedPermissions => [];

    /// <summary>
    /// Minimum number of permission references the scan must find: a non-vacuity guard, so a renamed
    /// assembly or attribute cannot let the coverage check pass without inspecting anything. Override
    /// to the repo's known floor.
    /// </summary>
    protected virtual int MinimumNamedPermissions => 1;

    [Fact]
    public void NamedPermissions_ShouldBe_GrantedToARole() =>
        FluentActions.Invoking(() => ArchitectureRules.NamedPermissionsAreGranted(
                TargetAssemblies, DeclaredPermissions, AdditionalReferencedPermissions))
            .Should().NotThrow();

    [Fact]
    public void ScannedPermissionSet_IsNotEmpty() =>
        ArchitectureRules.CountNamedPermissions(TargetAssemblies).Should().BeGreaterThanOrEqualTo(
            MinimumNamedPermissions,
            "the permission references must actually be inspected (expected at least {0})",
            MinimumNamedPermissions);
}
