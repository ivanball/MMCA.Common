using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// The prerender half of <c>DataGridListPageBase</c>'s loading contract, exercised through the real
/// <see cref="UserAdminList{TUser}"/> page. The SSR prerender pass runs no grid load (MudDataGrid
/// fetches from <c>OnAfterRenderAsync</c>, which a static render never reaches), so the prerendered
/// HTML used to carry "No users found." for the one to two seconds before the interactive render
/// loaded the rows. A non-interactive render must show the loading state and never the empty state.
/// Separate from <see cref="UserAdminListTests"/> because bUnit's <c>SetRendererInfo</c> configures
/// the whole test context, not a single render; the interactive companion (empty state only after a
/// completed zero-row load) lives there.
/// </summary>
public sealed class UserAdminListPrerenderTests : BunitTestBase
{
    private const string EmptyMessage = "No users found.";

    private int _fetches;

    public UserAdminListPrerenderTests()
    {
        Services.AddSingleton(Mock.Of<IUserAdminActionsUIService>());
        Services.AddSingleton(Mock.Of<IAppDialogService>());

        // isInteractive: false puts the renderer in the SSR prerender mode this class asserts on.
        // Last, because it freezes the bUnit service provider.
        ConfigureDataGridListPageHost(isInteractive: false);
    }

    [Fact]
    public void NonInteractiveRender_ShowsTheLoadingState_NotTheEmptyState()
    {
        RenderMudProviders();

        var cut = RenderUnderTest<UserAdminList<UserAdminListTests.TestUser>>(p => p
            .Add(x => x.AssignableRoles, ["Member"])
            .Add(x => x.FetchPage, NoAccounts));

        // bUnit runs OnAfterRenderAsync even for a non-interactive renderer, so the grid's zero-row
        // load completes here, which a real prerender never reaches. Waiting for it is the stronger
        // form of the assertion: even a finished empty answer must not paint the empty state while
        // the page is not interactive.
        cut.WaitForAssertion(() => _fetches.Should().BePositive());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".mud-table-loading").Should().NotBeEmpty("the grid renders its loading bar while prerendering");
            cut.Markup.Should().NotContain(EmptyMessage, "the prerendered HTML must not claim the list is empty");
        });
    }

    // Zero accounts: the answer that, once a load completes, legitimately shows the empty state.
    private Task<Result<(IReadOnlyList<UserAdminListTests.TestUser> Items, int TotalItems)>> NoAccounts(
        Dictionary<string, (string Operator, string Value)> filters,
        int pageNumber,
        int pageSize,
        string? sortColumn,
        string? sortDirection,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromResult(Result.Success<(IReadOnlyList<UserAdminListTests.TestUser> Items, int TotalItems)>(([], 0)));
    }
}
