using AwesomeAssertions;
using Bunit.Extensions.WaitForHelpers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI.Pages;
using MMCA.Common.UI.Components.PageState;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// Proves <see cref="DetailPageLoadStateTestsBase{TPage, TId, TDto}"/> can fail: each inherited fact
/// that pins a wiring mistake throws when run against a page that makes that mistake, while a fact
/// the mistake does not touch still passes in the same harness (so the throw is the assertion, not a
/// broken set-up). The probe subclasses are private, so xUnit never collects their inherited facts;
/// the tests call those facts directly.
/// </summary>
public sealed class DetailPageLoadStateTestsBaseFailureTests
{
    [Fact]
    public async Task APageThatRendersNotFoundForEveryFailure_FailsTheRetryFact()
    {
        await using var probe = new Probe<NotFoundForEveryFailurePage>();

        Action control = probe.WhenTheRecordIsNotFound_RendersTheNotFoundState;
        control.Should().NotThrow("a real 404 still renders the not-found text on this page");

        await using var sut = new Probe<NotFoundForEveryFailurePage>();
        Action act = sut.WhenTheFetchFails_RendersTheErrorWithRetry_AndRetryFetchesAgain;

        act.Should().Throw<WaitForFailedException>(
            "a transport failure that reads \"not found\" tells the reader not to try again, so the load-failed alert never appears");
    }

    [Fact]
    public async Task APageThatIgnoresTheLoadGuard_FailsTheSupersededLoadFact()
    {
        await using var probe = new Probe<IgnoresLoadGuardPage>();

        Action control = probe.WhenTheFetchFails_RendersTheErrorWithRetry_AndRetryFetchesAgain;
        control.Should().NotThrow("the guard only matters when two loads overlap");

        await using var sut = new Probe<IgnoresLoadGuardPage>();
        Func<Task> act = sut.WhenTheRouteIdChangesBeforeTheFirstFetchCompletes_TheStaleResultIsDropped;

        // The stale record lands where the newer route's record should be, so the fact's final
        // markup assertions name the record the reader left.
        var stale = ProbeDetailPageFetch.Record(ProbeDetailPageFetch.LoadedId).Name;
        await act.Should().ThrowAsync<Exception>().WithMessage("*" + stale + "*");
    }

    /// <summary>Collapses a failed load into the not-found body, so Retry never renders.</summary>
    public sealed class NotFoundForEveryFailurePage : ProbeDetailPage
    {
        protected override DetailLoadState RenderedState(DetailLoadState state) =>
            state == DetailLoadState.Failed ? DetailLoadState.NotFound : state;
    }

    /// <summary>Adopts every successful response, including one for a route id the reader already left.</summary>
    public sealed class IgnoresLoadGuardPage : ProbeDetailPage
    {
        protected override bool HonorsLoadGuard => false;
    }

    private sealed class Probe<TPage> : DetailPageLoadStateTestsBase<TPage, int, ProbeRecord>
        where TPage : ProbeDetailPage
    {
        private readonly ProbeDetailPageFetch _fetch;

        public Probe() => _fetch = new ProbeDetailPageFetch(Services);

        protected override int LoadedId => ProbeDetailPageFetch.LoadedId;

        protected override int OtherId => ProbeDetailPageFetch.OtherId;

        protected override string NotFoundText => ProbeDetailPage.NotFoundText;

        protected override ProbeRecord CreateRecord(int id) => ProbeDetailPageFetch.Record(id);

        protected override string RecordMarker(int id) => ProbeDetailPageFetch.Record(id).Name;

        protected override void SetupFetch(int id, Func<Task<Result<ProbeRecord>>> fetch) => _fetch.Setup(id, fetch);
    }
}
