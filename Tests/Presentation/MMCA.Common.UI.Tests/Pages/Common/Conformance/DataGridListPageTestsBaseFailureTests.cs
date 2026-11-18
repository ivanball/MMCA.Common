using AwesomeAssertions;
using Bunit.Extensions.WaitForHelpers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// Proves <see cref="DataGridListPageTestsBase{TPage, TDto}"/> can fail: the failed-fetch fact throws
/// when run against a page that never binds the base's <c>LoadFailed</c> flag to the shell, while a
/// fact the mistake does not touch still passes in the same harness (so the throw is the assertion,
/// not a broken set-up). The probe subclass is private, so xUnit never collects its inherited facts;
/// the tests call those facts directly.
/// </summary>
public sealed class DataGridListPageTestsBaseFailureTests
{
    [Fact]
    public async Task APageThatDropsTheLoadFailedFlag_FailsTheRetryStateFact()
    {
        await using var probe = new Probe();

        Action control = probe.WithNoRows_RendersTheEmptyState;
        control.Should().NotThrow("an empty list still renders the empty state on this page");

        await using var sut = new Probe();
        Action act = sut.WhenTheFetchFails_RendersTheRetryState_NotTheEmptyState;

        act.Should().Throw<WaitForFailedException>(
            "without the failure flag a transport fault never reaches the shell, so the Retry alert never appears");
    }

    /// <summary>Leaves the base's <c>LoadFailed</c> flag unbound, so a failed fetch renders like an empty list.</summary>
    public sealed class DropsLoadFailedPage : ProbeListPage
    {
        protected override bool BindsLoadFailed => false;
    }

    private sealed class Probe : DataGridListPageTestsBase<DropsLoadFailedPage, ProbeRow>
    {
        private readonly ProbeListPageFetch _fetch;

        public Probe() => _fetch = new ProbeListPageFetch(Services);

        protected override string PageTitle => ProbeListPage.PageTitle;

        protected override string EmptyStateText => ProbeListPage.EmptyText;

        protected override string FirstRowMarker => ProbeListPageFetch.Rows[0].Name;

        protected override string? FirstRowHref => ProbeListPage.DetailHref(ProbeListPageFetch.Rows[0].Id);

        protected override IReadOnlyList<ProbeRow> CreateRows() => ProbeListPageFetch.Rows;

        protected override void SetupFetch(Func<Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>>> fetch) =>
            _fetch.Setup(fetch);
    }
}
