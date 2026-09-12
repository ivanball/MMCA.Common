using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Administration;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Forms;
using MMCA.Common.UI.Components.Lists;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services.Administration;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The UI half of ADR-116's user administration: a drop-in account roster with the operator actions
/// the framework's <c>Admin/Users</c> endpoints expose (lock, unlock, change role), plus an optional
/// delete. It is a COMPONENT, not a page: the app supplies the route, the authorization attribute
/// and the detail link, so the role that may reach it stays the app's decision.
/// </summary>
/// <remarks>
/// <para>
/// What the app supplies: a DTO implementing <see cref="IUserAdminDTO"/>, the required
/// <see cref="DetailHref"/> route builder, and optionally the roles an operator may assign, extra
/// columns or card lines, a delete callback, and its own <see cref="Localizer"/> for wording. Every
/// string this component renders is looked up in that localizer FIRST and falls back to
/// <see cref="UserAdminListResources"/>, so an app keeps its own vocabulary ("Deactivate" rather
/// than "Lock") without a parameter per word.
/// </para>
/// <para>
/// Two data paths. By default the list reads through
/// <see cref="IUserAdminUIService{TUserDto}"/> (registered by <c>AddUserAdministrationUI</c>),
/// whose endpoint takes a search term and a role. An app whose roster lives on its own endpoint,
/// with per-column filters and server sort, passes <see cref="FetchPage"/> instead and the service
/// is never resolved for reads. The three account ACTIONS always go through
/// <see cref="IUserAdminActionsUIService"/>, because they are the framework's endpoints either way.
/// </para>
/// <para>
/// The administration actions are hidden on the signed-in operator's own row. An operator who
/// locked or demoted themselves would lose the very capability needed to undo it, and the API has
/// no notion of "the caller", so the guard belongs here.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's administration-facing user DTO.</typeparam>
public partial class UserAdminList<TUser>
    where TUser : IUserAdminDTO
{
    /// <summary>
    /// The filter-bag key the search box is injected under when <see cref="FetchPage"/> owns the
    /// fetch. The app's delegate reads it to map the free-text box onto whatever its own endpoint
    /// calls that filter.
    /// </summary>
    public const string SearchFilterKey = "Search";

    /// <summary>The page title. Defaults to the localized "Users" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>Builds the route to one account's detail page. Required.</summary>
    [Parameter]
    [EditorRequired]
    public Func<TUser, string> DetailHref { get; set; } = default!;

    /// <summary>
    /// The roles an operator may assign, offered in the row menu as "Set role to X". Order them
    /// least to most privileged so the menu reads as a ladder. An empty list offers only
    /// lock and unlock.
    /// </summary>
    [Parameter] public IReadOnlyList<string> AssignableRoles { get; set; } = [];

    /// <summary>
    /// Maps a role value to its display name (Store shows "Administrator" for "Admin"). When null
    /// the raw value is shown. Used in the Role column, the mobile chip, and the set-role menu items
    /// and confirmations.
    /// </summary>
    [Parameter] public Func<string, string>? RoleLabel { get; set; }

    /// <summary>Extra grid columns rendered directly after the Email column.</summary>
    [Parameter] public RenderFragment? Columns { get; set; }

    /// <summary>Extra grid columns rendered after the status column, before the actions column.</summary>
    [Parameter] public RenderFragment? TrailingColumns { get; set; }

    /// <summary>Extra lines on the mobile card, rendered between the email link and the role/status chips.</summary>
    [Parameter] public RenderFragment<TUser>? CardContent { get; set; }

    /// <summary>
    /// Overrides the data source with the app's own paged endpoint (per-column filters and server
    /// sort included). When null the list reads through
    /// <see cref="IUserAdminUIService{TUserDto}"/>; the account actions always go through
    /// <see cref="IUserAdminActionsUIService"/> either way. The free-text search box is passed
    /// through the filter bag under <see cref="SearchFilterKey"/>.
    /// </summary>
    [Parameter]
    public Func<
        Dictionary<string, (string Operator, string Value)>,
        int,
        int,
        string?,
        string?,
        CancellationToken,
        Task<Result<(IReadOnlyList<TUser> Items, int TotalItems)>>>? FetchPage
    { get; set; }

    /// <summary>
    /// Whether the Email and Role columns are sortable. Defaults to <see langword="false"/>, because
    /// the framework's administration endpoint ignores sort and a sortable header would lie; an app
    /// listing through <see cref="FetchPage"/> from a sorting endpoint turns it on.
    /// </summary>
    [Parameter] public bool Sortable { get; set; }

    /// <summary>Whether the debounced search box is rendered above the list.</summary>
    [Parameter] public bool ShowSearch { get; set; } = true;

    /// <summary>
    /// The app's own delete call. When set, every row and card offers a Delete button behind the
    /// shared confirmation dialog; when null no delete is offered at all (account erasure is its own
    /// endpoint under its own authorization rule, so the framework does not assume one exists).
    /// </summary>
    [Parameter] public Func<TUser, Task<Result>>? OnDelete { get; set; }

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="UserAdminListResources"/>. This is how an app keeps its own
    /// wording without a parameter per word.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    [Inject] private IUserAdminActionsUIService Actions { get; set; } = default!;

    [Inject] private IAppDialogService Dialogs { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;

    [Inject] private IStringLocalizer<UserAdminListResources> L { get; set; } = default!;

    private UserIdentifierType? _currentUserId;

    private MudDataGrid<TUser>? _dataGrid;
    private MobileInfiniteScrollList<TUser>? _infiniteList;
    private DeleteConfirmation _deleteConfirm = default!;
    private string _searchString = string.Empty;

    /// <inheritdoc />
    protected override string Title => Heading ?? T("Title");

    /// <inheritdoc />
    protected override MudDataGrid<TUser>? GridRef => _dataGrid;

    /// <summary>
    /// The reading service, resolved on demand rather than injected so an app that supplies
    /// <see cref="FetchPage"/> never needs it registered at all.
    /// </summary>
    private IUserAdminUIService<TUser> Users =>
        ServiceProvider.GetService<IUserAdminUIService<TUser>>()
            ?? throw new InvalidOperationException(
                $"UserAdminList<{typeof(TUser).Name}> lists through IUserAdminUIService<{typeof(TUser).Name}>, which is not registered. "
                + $"Call services.AddUserAdministrationUI<{typeof(TUser).Name}>() after AddUIShared, or supply the FetchPage parameter to list from your own endpoint.");

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        _currentUserId = authState.User.GetUserId();
    }

    /// <summary>
    /// Resolves one key: the app's own localizer when it carries the key, otherwise this
    /// component's resources. Arguments go through the localizer's indexer rather than
    /// <c>string.Format</c>, so a translation is free to reorder its placeholders.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="args">Format arguments, if the string takes any.</param>
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

    /// <summary>The display name of a role value, or the raw value when the app supplied no map.</summary>
    /// <param name="role">The role value as the API states it.</param>
    private string RoleText(string role) => RoleLabel?.Invoke(role) ?? role;

    /// <summary>
    /// Whether the administration actions are offered for <paramref name="user"/>. They are hidden
    /// on the operator's own row: locking or demoting yourself removes the capability to undo it.
    /// </summary>
    /// <param name="user">The row.</param>
    private bool CanAdminister(TUser user) =>
        _currentUserId is null || !EqualityComparer<UserIdentifierType>.Default.Equals(user.UserId, _currentUserId.Value);

    /// <inheritdoc />
    protected override void SaveFilters(Dictionary<string, string> filters) =>
        filters["search"] = _searchString;

    /// <inheritdoc />
    protected override void RestoreFilters(IReadOnlyDictionary<string, string> filters) =>
        _searchString = filters.GetValueOrDefault("search") ?? string.Empty;

    private Task ReloadActiveLayoutAsync()
        => ListPageActions.ReloadActiveLayoutAsync(IsMobile, _infiniteList, _dataGrid);

    /// <summary>Retries a failed grid fetch from the inline error state (<c>LoadFailed</c>).</summary>
    private Task RetryLoadAsync() => GridRef?.ReloadServerData() ?? Task.CompletedTask;

    private async Task OnSearchChanged(string value)
    {
        _searchString = value;
        await ReloadActiveLayoutAsync();
    }

    private Task<GridData<TUser>> LoadServerData(GridState<TUser> state, CancellationToken cancellationToken)
    {
        if (FetchPage is { } fetch)
        {
            return LoadServerDataAsync(state, fetch, InjectSearchFilter);
        }

        return LoadServerDataAsync(
            state,
            (filters, page, size, _, _, ct) => Users.GetPagedAsync(
                page,
                size,
                SearchTermFrom(filters),
                FilterValue(filters, nameof(IUserAdminDTO.Role)),
                ct));
    }

    // ── Mobile ──
    private Task<Result<(IReadOnlyList<TUser> Items, int TotalItems)>> FetchMobilePage(int page, int pageSize, CancellationToken ct)
    {
        if (FetchPage is { } fetch)
        {
            var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.Ordinal);
            InjectSearchFilter(filters);
            return fetch(filters, page, pageSize, "Email", "asc", ct);
        }

        var searchTerm = !string.IsNullOrWhiteSpace(_searchString) ? _searchString : null;
        return Users.GetPagedAsync(page, pageSize, searchTerm, role: null, ct);
    }

    /// <summary>
    /// Adds the free-text box to the filter bag the app's own fetch delegate reads, under the
    /// documented <see cref="SearchFilterKey"/>. A blank box adds nothing, so the delegate never has
    /// to distinguish "empty" from "absent".
    /// </summary>
    /// <param name="filters">The grid's filter bag.</param>
    private void InjectSearchFilter(Dictionary<string, (string Operator, string Value)> filters)
    {
        if (!string.IsNullOrWhiteSpace(_searchString))
        {
            filters[SearchFilterKey] = ("contains", _searchString);
        }
    }

    /// <summary>
    /// The search term the administration endpoint is called with: the free-text box when it holds
    /// anything, otherwise the Email column's own filter, so both affordances reach the one
    /// parameter the endpoint takes.
    /// </summary>
    /// <param name="filters">The grid's filter bag.</param>
    private string? SearchTermFrom(Dictionary<string, (string Operator, string Value)> filters) =>
        !string.IsNullOrWhiteSpace(_searchString)
            ? _searchString
            : FilterValue(filters, nameof(IUserAdminDTO.Email));

    /// <summary>
    /// Reads one column filter's value out of the grid's filter bag. The operator is ignored: the
    /// administration endpoint takes a search term and a role, not a per-column comparison, so the
    /// value is all it can honor.
    /// </summary>
    /// <param name="filters">The grid's filter bag.</param>
    /// <param name="property">The column's property name.</param>
    private static string? FilterValue(Dictionary<string, (string Operator, string Value)> filters, string property) =>
        filters is not null && filters.TryGetValue(property, out var filter) && !string.IsNullOrWhiteSpace(filter.Value)
            ? filter.Value
            : null;

    // ── Administration (ADR-116) ──
    private async Task ToggleLockAsync(TUser user)
    {
        var locking = !user.IsLocked;

        var confirmed = await Dialogs.ConfirmAsync(
            locking ? T("Confirm.Lock.Title") : T("Confirm.Unlock.Title"),
            locking ? T("Confirm.Lock.Message", user.Email) : T("Confirm.Unlock.Message", user.Email),
            locking ? T("Action.Lock") : T("Action.Unlock"),
            T("Button.Cancel"));

        if (!confirmed)
        {
            return;
        }

        var result = locking
            ? await Actions.LockAsync(user.UserId)
            : await Actions.UnlockAsync(user.UserId);

        if (result.IsFailure)
        {
            Toast.Error(locking ? T("Snackbar.LockFailed") : T("Snackbar.UnlockFailed"));
            return;
        }

        Toast.Success(locking ? T("Snackbar.Locked") : T("Snackbar.Unlocked"));
        await ReloadActiveLayoutAsync();
    }

    private async Task ChangeRoleAsync(TUser user, string role)
    {
        var label = RoleText(role);

        var confirmed = await Dialogs.ConfirmAsync(
            T("Confirm.Role.Title"),
            T("Confirm.Role.Message", user.Email, label),
            T("Confirm.Role.Confirm"),
            T("Button.Cancel"));

        if (!confirmed)
        {
            return;
        }

        var result = await Actions.SetRoleAsync(user.UserId, role);
        if (result.IsFailure)
        {
            Toast.Error(T("Snackbar.RoleChangeFailed"));
            return;
        }

        Toast.Success(T("Snackbar.RoleChanged", label));
        await ReloadActiveLayoutAsync();
    }

    // ── Common ──
    private Task DeleteUserAsync(TUser user)
        => ListPageActions.DeleteWithConfirmationAsync(
            _deleteConfirm,
            user.Email,
            () => OnDelete!(user),
            Toast,
            T("Snackbar.UserDeleted"),
            _ => T("Snackbar.DeleteUserFailed"),
            ReloadActiveLayoutAsync);
}
