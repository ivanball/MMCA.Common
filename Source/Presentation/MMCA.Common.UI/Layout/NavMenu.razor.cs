using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common;
using MudBlazor;

namespace MMCA.Common.UI.Layout;

/// <summary>
/// The shared sidebar: the account block, then the General, My Account and Administration sections
/// built from every registered <see cref="Common.Interfaces.IUIModule"/>'s nav items, filtered by the
/// signed-in user and arranged into groups (ADR-067).
/// </summary>
public partial class NavMenu
{
    private ClaimsPrincipal? _user;
    private List<Type> _appBarComponents = [];
    private List<Type> _publicAppBarComponents = [];
    private readonly Dictionary<string, NavGroup> _groupDescriptions = [];
    private List<NavUnit> _generalUnits = [];
    private List<NavUnit> _userUnits = [];
    private List<NavUnit> _adminUnits = [];
    private bool _showSessionsLink;
    private bool _subscribed;

    // ADR-027: Title and Group are resource keys resolved at render time against the item's
    // TitleResource resx pair, so the menu follows the active culture; a key the resource type does
    // not declare renders as the raw string.
    private string Display(NavItem item)
    {
        var localized = LocalizerFactory.Create(item.TitleResource)[item.Title];
        return localized.ResourceNotFound ? item.Title : localized.Value;
    }

    // The heading resolves against the resource of the group's first REGISTERED item, as it always
    // has, so ordering the items inside the group never changes which resx answers the key.
    private string DisplayGroup(NavUnit unit)
    {
        if (unit.Key is null)
        {
            return string.Empty;
        }

        var localized = LocalizerFactory.Create(unit.TitleResource)[unit.Key];
        return localized.ResourceNotFound ? unit.Key : localized.Value;
    }

    // Groups the section by key in first-appearance order, then sorts the units by their described
    // Order and the items inside each unit by NavItem.Order. Both sorts are stable, so a host that
    // describes no group and orders no item keeps exactly the former arrangement.
    private List<NavUnit> Arrange(IEnumerable<NavItem> items) =>
        [.. items
            .GroupBy(item => item.Group)
            .Select(group =>
            {
                var description = group.Key is null ? null : _groupDescriptions.GetValueOrDefault(group.Key);
                return new NavUnit(
                    group.Key,
                    group.First().TitleResource,
                    description?.Icon ?? Icons.Material.Filled.Folder,
                    description?.Expanded ?? false,
                    description?.Order ?? 0,
                    [.. group.OrderBy(item => item.Order)]);
            })
            .OrderBy(unit => unit.Order)
        ];

    private async Task HandleLogoutAsync()
    {
        await AuthService.LogoutAsync();
        Navigation.NavigateTo("/login", forceLoad: true);
    }

    protected override async Task OnInitializedAsync()
    {
        _appBarComponents = [.. UIModules.SelectMany(m => m.AppBarComponentTypes)];
        _publicAppBarComponents = [.. UIModules.SelectMany(m => m.PublicAppBarComponentTypes)];

        // First registered description of a key wins, the same rule as NotFoundContentType.
        foreach (var description in UIModules.SelectMany(m => m.NavGroups))
        {
            _groupDescriptions.TryAdd(description.Key, description);
        }

        await RebuildFromAuthStateAsync(AuthStateProvider.GetAuthenticationStateAsync());

        // The sections are computed from the user, so an in-circuit sign-out (a failed silent refresh
        // raises AuthenticationStateChanged without a reload) must recompute them, or the admin and
        // user sections stay visible to a signed-out visitor.
        AuthStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _subscribed = true;
    }

    // Event-handler signature; the rebuild task observes its own teardown race, so the explicit
    // discard is safe and avoids the async-void crash-the-process mode (VSTHRD100).
    private void OnAuthenticationStateChanged(Task<AuthenticationState> authStateTask) =>
        _ = RefreshFromAuthStateAsync(authStateTask);

    private async Task RefreshFromAuthStateAsync(Task<AuthenticationState> authStateTask)
    {
        try
        {
            await InvokeAsync(async () =>
            {
                await RebuildFromAuthStateAsync(authStateTask);
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
            // Renderer torn down between the auth-state event and the dispatch.
        }
    }

    private async Task RebuildFromAuthStateAsync(Task<AuthenticationState> authStateTask)
    {
        var authState = await authStateTask;
        _user = authState.User;

        var sessionsRole = LayoutSettingsOptions.Value.SessionsNavRequiredRole;
        _showSessionsLink = string.IsNullOrWhiteSpace(sessionsRole) || _user?.IsInRole(sessionsRole) == true;

        var allItems = UIModules
            .SelectMany(m => m.NavItems)
            .Where(item => (item.RequiredRole is null || _user?.IsInRole(item.RequiredRole) == true)
                && (item.RequiredClaim is null || _user?.HasClaim(c => c.Type == item.RequiredClaim) == true)
                && (item.RequiredPermission is null || _user.HasPermissionClaim(item.RequiredPermission)))
            .ToList();

        _generalUnits = Arrange(allItems.Where(i => i.Section is NavSection.General));
        _userUnits = Arrange(allItems.Where(i => i.Section is NavSection.User));
        _adminUnits = Arrange(allItems.Where(i => i.Section is NavSection.Admin));
    }

    private sealed record NavUnit(string? Key, Type TitleResource, string Icon, bool Expanded, int Order, IReadOnlyList<NavItem> Items);

    /// <summary>Unsubscribes from auth-state changes; safe to call more than once.</summary>
    /// <param name="disposing">Whether managed state is being released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !_subscribed)
        {
            return;
        }

        AuthStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _subscribed = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
