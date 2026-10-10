using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Administration;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="UserAdminList{TUser}"/> component (the UI half of
/// ADR-116): rows and the empty/failed states, the one data path (the required <c>FetchPage</c>
/// delegate, which receives every column filter with its operator and the search box beside it),
/// the accessible shape of the row action menu, the confirm-then-act flows for lock and role change,
/// the own-row guard, the opt-in delete, the extra-column extension point, the app-localizer
/// override, and the mobile card layout.
/// </summary>
public sealed class UserAdminListTests : BunitTestBase
{
    /// <summary>
    /// A principal carrying the id of the first seeded row, used to prove the administration
    /// actions are hidden on the operator's OWN row (self-lock and self-demotion are one-way doors).
    /// </summary>
    private static readonly ClaimsPrincipal SignedInAsAda = new(new ClaimsIdentity(
        [new Claim(AuthClaimTypes.Subject, "1")],
        authenticationType: "TestAuth"));

    private static readonly BrowserWindowSize PhoneViewport = new() { Width = 390, Height = 844 };

    private readonly Mock<IUserAdminActionsUIService> _actions = new();
    private readonly Mock<IAppDialogService> _dialogs = new();

    // Every filter bag FetchPage was called with, copied at call time, newest last.
    private readonly List<Dictionary<string, (string Operator, string Value)>> _fetchedFilters = [];

    private Result<(IReadOnlyList<TestUser> Items, int TotalItems)> _page =
        Result.Success<(IReadOnlyList<TestUser> Items, int TotalItems)>(([], 0));

    public UserAdminListTests()
    {
        Services.AddSingleton(_actions.Object);
        Services.AddSingleton(_dialogs.Object);

        // Wires the list-page state services, the inert IBrowserViewportService stub (keeping
        // IsMobile deterministically false so the component stays on the desktop grid branch) and
        // the persistent-state double, then sets the interactive renderer info LoadServerDataAsync
        // consults. Must be last: it freezes the service provider.
        ConfigureDataGridListPageHost();

        SetupUsers(Ada, Grace);

        _actions.Setup(x => x.LockAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _actions.Setup(x => x.UnlockAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _actions.Setup(x => x.SetRoleAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    /// <summary>
    /// The app's own administration DTO. Public so the generic component can be closed over it. Its
    /// identifier is the <c>IBaseDTO</c> <c>Id</c> the positional record already declares, and it
    /// maps its own spelling of the lock state onto <see cref="IUserAdminDTO"/> explicitly, which is
    /// the adoption path a consumer takes.
    /// </summary>
    public sealed record TestUser(int Id, string Email, string Role, bool Locked) : IUserAdminDTO
    {
        bool IUserAdminDTO.IsLocked => Locked;
    }

    private static TestUser Ada => new(1, "ada@example.com", "Admin", Locked: false);

    private static TestUser Grace => new(2, "grace@example.com", "Member", Locked: false);

    private void SetupUsers(params TestUser[] users) =>
        _page = Result.Success<(IReadOnlyList<TestUser> Items, int TotalItems)>((users, users.Length));

    /// <summary>The FetchPage delegate every render passes: records the filter bag, answers the arranged page.</summary>
    private Task<Result<(IReadOnlyList<TestUser> Items, int TotalItems)>> FetchPage(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        CancellationToken cancellationToken)
    {
        _fetchedFilters.Add(new Dictionary<string, (string Operator, string Value)>(filters, StringComparer.Ordinal));
        return Task.FromResult(_page);
    }

    private IRenderedComponent<UserAdminList<TestUser>> RenderList(
        ClaimsPrincipal? principal = null,
        Action<ComponentParameterCollectionBuilder<UserAdminList<TestUser>>>? extra = null,
        bool withFetchPage = true) =>
        RenderAs<UserAdminList<TestUser>>(
            principal ?? Anonymous,
            p =>
            {
                p.Add(x => x.DetailHref, u => string.Create(CultureInfo.InvariantCulture, $"/users/{u.Id}"));
                p.Add(x => x.AssignableRoles, ["Member", "Admin"]);
                if (withFetchPage)
                {
                    p.Add(x => x.FetchPage, FetchPage);
                }

                extra?.Invoke(p);
            });

    // ── Rows and states ──
    [Fact]
    public void RendersRowsFromFetchPage()
    {
        RenderMudProviders();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("ada@example.com");
            cut.Markup.Should().Contain("grace@example.com");
            cut.Markup.Should().Contain("Member");
        });
    }

    [Fact]
    public void WithNoAccounts_RendersTheEmptyState()
    {
        SetupUsers();
        RenderMudProviders();

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No users found."));
    }

    /// <summary>
    /// The interactive companion of <see cref="UserAdminListPrerenderTests"/>: once the renderer is
    /// interactive, a completed zero-row load ends the loading state and shows the empty state.
    /// </summary>
    [Fact]
    public void Interactive_AfterACompletedZeroRowLoad_ShowsTheEmptyState_NotTheLoadingState()
    {
        SetupUsers();
        RenderMudProviders();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No users found.");
            cut.FindAll(".mud-table-loading").Should().BeEmpty("the load has completed");
        });
    }

    /// <summary>
    /// A failed fetch renders zero rows, which is visually identical to a genuinely empty list, so
    /// the shared <c>ListNoRecordsContent</c> must take over with its inline error and Retry.
    /// </summary>
    [Fact]
    public void WhenTheFetchFails_RendersTheInlineLoadFailureInsteadOfTheEmptyState()
    {
        _page = Result.Failure<(IReadOnlyList<TestUser> Items, int TotalItems)>(
            Error.Failure("Users.LoadFailed", "boom"));
        RenderMudProviders();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Could not load this list");
            cut.Markup.Should().NotContain("No users found.");
        });
    }

    [Fact]
    public void ALockedAccount_RendersTheLockedChip()
    {
        SetupUsers(Grace with { Locked = true });
        RenderMudProviders();

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Locked"));
    }

    // ── The one data path: FetchPage ──
    /// <summary>
    /// There is no default data path any more, so a list rendered without its data source must say
    /// so at once, naming the parameter, rather than painting an empty or failed grid that reads as
    /// "no users".
    /// </summary>
    [Fact]
    public void WithoutFetchPage_FailsLoudly_NamingTheParameter()
    {
        RenderMudProviders();

        var render = () => RenderList(withFetchPage: false);

        render.Should().Throw<InvalidOperationException>().WithMessage("*FetchPage*");
    }

    /// <summary>
    /// Every column filter reaches the app's paged endpoint exactly as the grid states it, operator
    /// included: the removed search-and-role path dropped the operator, so "Role not equals
    /// Organizer" arrived as "Role = Organizer" and a value-less operator arrived as nothing at all.
    /// </summary>
    /// <param name="property">The filtered column.</param>
    /// <param name="operator">The MudBlazor filter operator.</param>
    /// <param name="value">The filter value (blank for a value-less operator).</param>
    [Theory]
    [InlineData("Role", "not equals", "Organizer")]
    [InlineData("Email", "contains", "@x")]
    [InlineData("Role", "is empty", "")]
    public async Task AColumnFilter_ReachesFetchPageVerbatim(string property, string @operator, string value)
    {
        RenderMudProviders();
        var cut = RenderList();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("ada@example.com"));

        await ApplyColumnFilterAsync(cut, property, @operator, value);

        _fetchedFilters[^1].Should().ContainKey(property)
            .WhoseValue.Should().Be((@operator, value));
    }

    /// <summary>
    /// The search box maps onto the Email column, so with no column filter it travels under the
    /// plain property key the generic paged endpoint resolves (a user DTO has no "Search" field for
    /// the server to filter by).
    /// </summary>
    [Fact]
    public void TheSearchBox_ReachesFetchPageUnderTheEmailKey()
    {
        RenderMudProviders();
        var cut = RenderList();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("ada@example.com"));

        cut.Find("input[placeholder='Search by exact email address...']").Input("smith");

        // The search box debounces for 300ms before reloading the grid.
        cut.WaitForAssertion(
            () => _fetchedFilters[^1].Should().ContainKey(nameof(IUserAdminDTO.Email))
                .WhoseValue.Should().Be(("contains", "smith")),
            TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The search box and the Email column's own filter are two filters on one property: the column
    /// keeps the plain key and the search box moves to the aliased key the server ANDs with it, so
    /// neither overwrites the other.
    /// </summary>
    [Fact]
    public async Task TheSearchBoxAndAnEmailColumnFilter_BothReachFetchPage()
    {
        RenderMudProviders();
        var cut = RenderList();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("ada@example.com"));

        var grid = cut.FindComponent<MudDataGrid<TestUser>>();
        await cut.InvokeAsync(() => grid.Instance.FilterDefinitions.Add(Filter("Email", "contains", "@x")));
        await cut.Find("input[placeholder='Search by exact email address...']")
            .InputAsync(new ChangeEventArgs { Value = "bob" });

        var searchKey = QueryFilterKeys.Alias(nameof(IUserAdminDTO.Email), QueryFilterKeys.SearchTag);
        await cut.WaitForAssertionAsync(
            () =>
            {
                var filters = _fetchedFilters[^1];
                filters.Should().ContainKey(nameof(IUserAdminDTO.Email))
                    .WhoseValue.Should().Be(("contains", "@x"));
                filters.Should().ContainKey(searchKey)
                    .WhoseValue.Should().Be(("contains", "bob"));
            },
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheMobileList_FetchesThroughFetchPage()
    {
        RenderMudProviders();
        var cut = RenderList();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("ada@example.com"));

        var before = _fetchedFilters.Count;
        await GoToPhoneAsync(cut);

        _fetchedFilters.Count.Should().BeGreaterThan(before, "the mobile card list fetches through the same delegate");
    }

    // ── Extra columns ──
    /// <summary>
    /// The extension point a consumer uses for its own grid columns (ADC's "Signed in" column):
    /// whatever <c>TrailingColumns</c> carries renders inside the grid, between the status and the
    /// actions columns.
    /// </summary>
    [Fact]
    public void TrailingColumns_RenderInsideTheGrid()
    {
        RenderMudProviders();

        var cut = RenderList(extra: p => p.Add(x => x.TrailingColumns, builder =>
        {
            builder.OpenComponent<TemplateColumn<TestUser>>(0);
            builder.AddAttribute(1, nameof(TemplateColumn<>.Title), "Signed in");
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() => cut.Find(".mud-table-head").TextContent.Should().Contain("Signed in"));
    }

    private static async Task ApplyColumnFilterAsync(
        IRenderedComponent<UserAdminList<TestUser>> cut,
        string property,
        string @operator,
        string value)
    {
        var grid = cut.FindComponent<MudDataGrid<TestUser>>();
        await cut.InvokeAsync(() =>
        {
            grid.Instance.FilterDefinitions.Add(Filter(property, @operator, value));
            return grid.Instance.ReloadServerData();
        });
    }

    private static IFilterDefinition<TestUser> Filter(string propertyName, string @operator, string value)
    {
        var column = new Mock<Column<TestUser>>();
        column.Setup(c => c.PropertyName).Returns(propertyName);

        var definition = new Mock<IFilterDefinition<TestUser>>();
        definition.SetupGet(f => f.Column).Returns(column.Object);
        definition.SetupGet(f => f.Operator).Returns(@operator);
        definition.SetupGet(f => f.Value).Returns(value);
        return definition.Object;
    }

    // ── The action menu's accessible shape ──
    /// <summary>
    /// The row's action menu must be one button, not a button inside a button: MudBlazor wraps
    /// custom <c>ActivatorContent</c> in its own div carrying <c>role="button"</c>, so an icon
    /// button placed inside it is a control nested in a control (axe nested-interactive). The
    /// built-in icon activator IS the button.
    /// </summary>
    [Fact]
    public void TheActionMenuActivator_IsTheButtonItselfNotAWrapperAroundOne()
    {
        RenderMudProviders();

        var cut = RenderList();
        cut.WaitForAssertion(() =>
            cut.Find("button[aria-label='Administration actions for grace@example.com']"));

        cut.FindAll(".mud-menu-activator").Should().BeEmpty();
        cut.FindAll("[role='button'] button").Should().BeEmpty();
    }

    /// <summary>
    /// Clicking the activator must OPEN the menu. A custom activator that never calls
    /// <c>MenuContext.ToggleAsync</c> renders a button that does nothing, which is how the lock and
    /// set-role actions once shipped unreachable.
    /// </summary>
    [Fact]
    public void ClickingTheActivator_OpensTheAdministrationMenu()
    {
        var providers = RenderMudProviders();

        var cut = RenderList();
        cut.WaitForAssertion(() =>
            cut.Find("button[aria-label='Administration actions for grace@example.com']"));

        cut.Find("button[aria-label='Administration actions for grace@example.com']").Click();

        providers.Popover.WaitForAssertion(
            () => providers.Popover.FindAll("[data-testid=toggle-lock]").Should().NotBeEmpty(),
            TimeSpan.FromSeconds(5));
    }

    // ── Lock and role, behind a confirmation ──
    [Fact]
    public void ConfirmingTheLockDialog_CallsLockAsync()
    {
        SetupUsers(Grace);
        Confirms(true);
        var providers = RenderMudProviders();

        var cut = OpenTheActionsMenu(providers);
        providers.Popover.Find("[data-testid=toggle-lock]").Click();

        cut.WaitForAssertion(() => _actions.Verify(x => x.LockAsync(2, It.IsAny<CancellationToken>()), Times.Once()));
    }

    [Fact]
    public void DecliningTheLockDialog_CallsNothing()
    {
        SetupUsers(Grace);
        Confirms(false);
        var providers = RenderMudProviders();

        var cut = OpenTheActionsMenu(providers);
        providers.Popover.Find("[data-testid=toggle-lock]").Click();

        cut.WaitForAssertion(() => _dialogs.Verify(
            x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once()));
        _actions.Verify(x => x.LockAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never());
        _actions.Verify(x => x.UnlockAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    /// <summary>
    /// The set-role items carry the account's own role as a disabled entry, so the enabled one is
    /// the move the operator can actually make.
    /// </summary>
    [Fact]
    public void ConfirmingASetRoleItem_CallsSetRoleAsyncWithThatRole()
    {
        SetupUsers(Grace);
        Confirms(true);
        var providers = RenderMudProviders();

        var cut = OpenTheActionsMenu(providers);
        var roleItems = providers.Popover.FindAll("[data-testid=set-role]");
        roleItems.Should().HaveCount(2, "the account's own role is offered as a disabled entry beside the move it can make");
        roleItems[1].Click();

        cut.WaitForAssertion(() => _actions.Verify(
            x => x.SetRoleAsync(2, "Admin", It.IsAny<CancellationToken>()), Times.Once()));
    }

    /// <summary>
    /// The operator's own row offers no lock or role action. Locking or demoting yourself removes
    /// the very capability needed to undo it, and the API has no notion of "the caller".
    /// </summary>
    [Fact]
    public void TheSignedInOperatorsOwnRow_OffersNoAdministrationMenu()
    {
        RenderMudProviders();

        var cut = RenderList(SignedInAsAda);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button[aria-label='Administration actions for grace@example.com']")
                .Should().NotBeEmpty();
            cut.FindAll("button[aria-label='Administration actions for ada@example.com']")
                .Should().BeEmpty();
        });
    }

    /// <summary>
    /// Delete follows the same own-row rule as lock and role: deleting yourself from the
    /// administration grid removes the capability to undo it (self-service deletion stays on the
    /// profile page).
    /// </summary>
    [Fact]
    public void TheSignedInOperatorsOwnRow_OffersNoDeleteButton()
    {
        RenderMudProviders();

        var cut = RenderList(SignedInAsAda, p => p.Add(x => x.OnDelete, _ => Task.FromResult(Result.Success())));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button[aria-label='Delete grace@example.com']").Should().NotBeEmpty();
            cut.FindAll("button[aria-label='Delete ada@example.com']").Should().BeEmpty();
        });
    }

    // ── Delete is opt-in ──
    [Fact]
    public void WithoutOnDelete_NoRowOffersADeleteButton()
    {
        RenderMudProviders();

        var cut = RenderList();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("grace@example.com"));

        cut.FindAll("button[aria-label='Delete grace@example.com']").Should().BeEmpty();
    }

    [Fact]
    public void WithOnDelete_ConfirmingTheDialog_InvokesTheCallback()
    {
        SetupUsers(Grace);
        var deleted = new List<TestUser>();
        var providers = RenderMudProviders();

        var cut = RenderList(extra: p => p.Add(
            x => x.OnDelete,
            user =>
            {
                deleted.Add(user);
                return Task.FromResult(Result.Success());
            }));
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Delete grace@example.com']"));

        cut.Find("button[aria-label='Delete grace@example.com']").Click();

        providers.Dialog.WaitForAssertion(() => providers.Dialog.Find("[data-testid=confirm-delete]"));
        providers.Dialog.Find("[data-testid=confirm-delete]").Click();

        cut.WaitForAssertion(() => deleted.Should().ContainSingle(u => u.Id == 2));
    }

    // ── Wording override ──
    /// <summary>
    /// An app keeps its own vocabulary by passing its localizer, without a parameter per word: a key
    /// it carries wins, and one it does not carry falls through to the component's own resources.
    /// </summary>
    [Fact]
    public void TheAppLocalizer_WinsForAKeyItCarries_AndFallsBackOtherwise()
    {
        RenderMudProviders();

        var cut = RenderList(extra: p => p.Add(
            x => x.Localizer,
            (IStringLocalizer?)new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Title"] = "User accounts",
                ["Action.Lock"] = "Deactivate account",
            })));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("User accounts");
            cut.Markup.Should().NotContain(">Users<");

            // Not in the app localizer, so the component's own resources answer it.
            cut.Markup.Should().Contain("Search by exact email address...");
        });
    }

    // ── Mobile card layout ──
    // Nothing in bUnit drives the breakpoint: the harness stubs IBrowserViewportService inert (so
    // IsMobile is deterministically false) and the component is itself the viewport observer, so the
    // switch is a direct call to the observer callback with an Xs breakpoint.
    private static async Task GoToPhoneAsync(IRenderedComponent<UserAdminList<TestUser>> cut)
    {
        await cut.InvokeAsync(() => cut.Instance.NotifyBrowserViewportChangeAsync(
            new BrowserViewportEventArgs(Guid.NewGuid(), PhoneViewport, Breakpoint.Xs)));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("View details"));
    }

    private async Task<IRenderedComponent<UserAdminList<TestUser>>> RenderOnAPhoneAsync(ClaimsPrincipal principal)
    {
        var cut = RenderList(principal);
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("grace@example.com"));
        await GoToPhoneAsync(cut);
        return cut;
    }

    /// <summary>
    /// Every card carries an explicit, labelled link into the account. The card itself is
    /// deliberately non-interactive (controls inside a card carrying <c>role="button"</c> are an axe
    /// nested-interactive violation), so without this button there is nothing on a phone that reads
    /// as "open this account".
    /// </summary>
    [Fact]
    public async Task OnAPhone_EveryCard_OffersAnExplicitDetailsLink()
    {
        RenderMudProviders();

        var cut = await RenderOnAPhoneAsync(Anonymous);

        cut.Find("a[aria-label='View details for grace@example.com']")
            .GetAttribute("href").Should().Be("/users/2");
    }

    [Fact]
    public async Task OnAPhone_ACardForAnotherAccount_OffersTheLabelledActionsMenu()
    {
        RenderMudProviders();

        var cut = await RenderOnAPhoneAsync(SignedInAsAda);

        cut.Find("button[aria-label='Administration actions for grace@example.com']")
            .TextContent.Should().Contain("Actions");
    }

    [Fact]
    public async Task OnAPhone_TheOperatorsOwnCard_OffersNoActionsMenu_AndNoCardIsAButton()
    {
        RenderMudProviders();

        var cut = await RenderOnAPhoneAsync(SignedInAsAda);

        cut.FindAll("button[aria-label='Administration actions for ada@example.com']").Should().BeEmpty();
        cut.FindAll("a[aria-label='View details for ada@example.com']").Should().NotBeEmpty();
        cut.FindAll(".mud-card[role='button']").Should().BeEmpty();
    }

    // ── Helpers ──
    private void Confirms(bool answer) =>
        _dialogs
            .Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(answer);

    private IRenderedComponent<UserAdminList<TestUser>> OpenTheActionsMenu(MudProviderHandles providers)
    {
        var cut = RenderList();
        cut.WaitForAssertion(() =>
            cut.Find("button[aria-label='Administration actions for grace@example.com']"));

        cut.Find("button[aria-label='Administration actions for grace@example.com']").Click();

        providers.Popover.WaitForAssertion(
            () => providers.Popover.FindAll("[data-testid=toggle-lock]").Should().NotBeEmpty(),
            TimeSpan.FromSeconds(5));

        return cut;
    }

    /// <summary>
    /// A minimal app localizer: it answers only the keys it was given and reports every other key as
    /// <c>ResourceNotFound</c>, which is the signal the component falls back on.
    /// </summary>
    private sealed class StubLocalizer(Dictionary<string, string> values) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, value, arguments), resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            values.Select(pair => new LocalizedString(pair.Key, pair.Value));
    }
}
