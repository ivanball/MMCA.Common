using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Administration;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The editor half of ADR-116's role administration: every permission the host declares, grouped by
/// area, with the ones this role already holds ticked and the ones its code grants ticked and
/// locked. Saving replaces the role's STORED set. It is a COMPONENT, not a page: the app supplies
/// the route, the authorization attribute and the link back to the roster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the catalog is offered.</b> The checkboxes come from the server's permission catalog,
/// which is the compiled universe, so nothing this screen can submit is a permission the server
/// would refuse as unknown. A free-text box would let an operator write a row no endpoint ever
/// checks.
/// </para>
/// <para>
/// <b>Two kinds of locked checkbox.</b> A permission the host compiled in for this role is ticked
/// and disabled, because removing it is a code change, not a data edit. And
/// <see cref="AdministrationPermissions.ManageRoles"/> is disabled everywhere: it is the permission
/// that guards this very screen, and the server refuses to store it, so offering it would be
/// offering an action guaranteed to fail.
/// </para>
/// <para>
/// Every string it renders is looked up in <see cref="Localizer"/> FIRST and falls back to
/// <see cref="RoleAdminEditResources"/>, the <see cref="UserAdminList{TUser}"/> contract unchanged.
/// </para>
/// </remarks>
public partial class RoleAdminEdit : ComponentBase, IDisposable
{
    /// <summary>The group a permission with no <c>area:capability</c> prefix is filed under.</summary>
    private const string GeneralAreaKey = "Group.General";

    private readonly CancellationTokenSource _cts = new();

    // IDE0028 suggests a collection expression here, but it cannot carry the Ordinal comparer that
    // keeps permission comparisons matching the registry's own.
#pragma warning disable IDE0028 // Collection initialization can be simplified
    private readonly HashSet<string> _compiled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stored = new(StringComparer.Ordinal);
#pragma warning restore IDE0028

    private IReadOnlyList<PermissionGroup> _groups = [];
    private Result? _loadResult;
    private Result? _saveResult;
    private string? _loadedRole;
    private bool _disposed;

    /// <summary>The role being edited. Required.</summary>
    [Parameter]
    [EditorRequired]
    public string Role { get; set; } = default!;

    /// <summary>The route back to the role roster. Required.</summary>
    [Parameter]
    [EditorRequired]
    public string ListHref { get; set; } = default!;

    /// <summary>The page title. Defaults to the localized "Permissions for {role}" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="RoleAdminEditResources"/>.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    [Inject] private IRoleAdminUIService Roles { get; set; } = default!;

    [Inject] private IToastService Toast { get; set; } = default!;

    [Inject] private IStringLocalizer<RoleAdminEditResources> L { get; set; } = default!;

    /// <summary>True while the initial (or retried) load is in flight.</summary>
    protected bool IsLoading { get; private set; }

    /// <summary>True while a save is in flight, which is what disables the save button.</summary>
    protected bool IsSaving { get; private set; }

    private string Title => Heading ?? T("Title", Role);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Loads on the first parameter set and again only when <see cref="Role"/> actually changes. A
    /// re-render from the parent must not throw away the operator's half-made edits.
    /// </summary>
    /// <returns>A task that completes once the form reflects the server.</returns>
    protected override Task OnParametersSetAsync() =>
        string.Equals(_loadedRole, Role, StringComparison.Ordinal) ? Task.CompletedTask : LoadAsync();

    /// <summary>
    /// Releases the cancellation source that supersedes an in-flight load when the component goes
    /// away.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Resolves one key: the app's own localizer when it carries the key, otherwise this
    /// component's resources. Arguments go through the localizer's indexer rather than
    /// <c>string.Format</c>, so a translation is free to reorder its placeholders.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="args">Format arguments, if the string takes any.</param>
    /// <returns>The resolved string.</returns>
    private string T(string key, params object[] args)
    {
        if (Localizer is not null)
        {
            var overridden = args.Length == 0 ? Localizer[key] : Localizer[key, args];
            if (!overridden.ResourceNotFound)
            {
                return overridden.Value;
            }
        }

        return (args.Length == 0 ? L[key] : L[key, args]).Value;
    }

    /// <summary>
    /// Loads the role and the catalog. The catalog decides which checkboxes exist; the role decides
    /// which of them are ticked, and which are locked because the code already grants them.
    /// </summary>
    /// <returns>A task that completes once the form reflects the server.</returns>
    private async Task LoadAsync()
    {
        IsLoading = true;
        _loadResult = null;
        _saveResult = null;
        _loadedRole = Role;

        try
        {
            var role = await Roles.GetAsync(Role, _cts.Token);
            if (role.IsFailure)
            {
                _loadResult = role;
                Reset();
                return;
            }

            var catalog = await Roles.GetCatalogAsync(_cts.Token);
            if (catalog.IsFailure)
            {
                _loadResult = catalog;
                Reset();
                return;
            }

            _compiled.Clear();
            _compiled.UnionWith(role.Value!.RegisteredPermissions);
            _stored.Clear();
            _stored.UnionWith(role.Value.StoredPermissions);

            _groups = Group(catalog.Value!.Permissions);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Reset()
    {
        _compiled.Clear();
        _stored.Clear();
        _groups = [];
    }

    /// <summary>
    /// Files each permission under the text before its first colon, which is the
    /// <c>area:capability</c> shape the registry documents. Areas and the permissions inside them
    /// are both ordered, so the form reads the same way on every load.
    /// </summary>
    /// <param name="permissions">The catalog's permissions.</param>
    /// <returns>The groups, in display order.</returns>
    private IReadOnlyList<PermissionGroup> Group(IEnumerable<string> permissions) =>
        [
            .. permissions
                .GroupBy(AreaOf, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PermissionGroup(
                    group.Key,
                    [.. group.Order(StringComparer.Ordinal)]))
        ];

    private string AreaOf(string permission)
    {
        var separator = permission.IndexOf(':', StringComparison.Ordinal);

        return separator > 0 ? permission[..separator] : T(GeneralAreaKey);
    }

    /// <summary>A permission is ticked when the code grants it or a stored row does.</summary>
    /// <param name="permission">The permission.</param>
    /// <returns>Whether its checkbox is ticked.</returns>
    private bool IsChecked(string permission) => _compiled.Contains(permission) || _stored.Contains(permission);

    /// <summary>Whether the checkbox is locked, and therefore contributes nothing to a save.</summary>
    /// <param name="permission">The permission.</param>
    /// <returns>Whether the checkbox is disabled.</returns>
    private bool IsReadOnly(string permission) =>
        _compiled.Contains(permission)
        || string.Equals(permission, AdministrationPermissions.ManageRoles, StringComparison.Ordinal);

    /// <summary>The reason a locked checkbox cannot be changed, or an empty string when it can.</summary>
    /// <param name="permission">The permission.</param>
    /// <returns>The hint rendered beside the checkbox.</returns>
    private string ReadOnlyHint(string permission)
    {
        if (_compiled.Contains(permission))
        {
            return T("Hint.Compiled");
        }

        return string.Equals(permission, AdministrationPermissions.ManageRoles, StringComparison.Ordinal)
            ? T("Hint.ManageRoles")
            : string.Empty;
    }

    private void Toggle(string permission, bool granted)
    {
        if (granted)
        {
            _stored.Add(permission);
        }
        else
        {
            _stored.Remove(permission);
        }
    }

    private async Task SaveAsync()
    {
        IsSaving = true;
        _saveResult = null;

        try
        {
            IReadOnlyList<string> permissions = [.. _stored.Order(StringComparer.Ordinal)];

            var result = await Roles.SetStoredPermissionsAsync(Role, permissions, _cts.Token);
            if (result.IsFailure)
            {
                _saveResult = result;
                Toast.Error(T("Snackbar.SaveFailed"));
                return;
            }

            // Re-seeded from the server's answer rather than from what was submitted: the two lists
            // it reports are disjoint, so a permission the host compiles in moves to the locked half
            // even if it arrived here ticked as stored.
            _compiled.Clear();
            _compiled.UnionWith(result.Value!.RegisteredPermissions);
            _stored.Clear();
            _stored.UnionWith(result.Value.StoredPermissions);

            Toast.Success(T("Snackbar.Saved"));
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>One <c>area:capability</c> area and the permissions filed under it.</summary>
    /// <param name="Area">The text before the first colon, or the localized "General".</param>
    /// <param name="Permissions">The area's permissions, ordered.</param>
    private sealed record PermissionGroup(string Area, IReadOnlyList<string> Permissions);
}
