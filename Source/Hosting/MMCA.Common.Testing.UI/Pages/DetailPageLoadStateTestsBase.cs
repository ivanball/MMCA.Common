using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for a consumer's detail page: one record loaded by its route id through a
/// <c>DetailPageBase</c> page that renders <c>DetailLoadStateContent</c>. The framework tests the
/// base and the component; what is pinned here is each page's own wiring of them: the spinner shows
/// while the fetch is pending, a not-found failure renders the page's not-found text, any other
/// failure renders the load-failed alert with a Retry that fetches again, and a response for a
/// route id the reader already left never overwrites the record the newer route owns.
/// </summary>
/// <remarks>
/// A consumer keeps a small sealed subclass naming the page, two ids, how a record renders, and how
/// its client double answers a fetch (the ADR-015 pattern). The subclass constructor registers the
/// page's services, including whatever the consumer's own bUnit base would have registered, since
/// this class derives the framework base directly. Because a test class has one base, a page whose
/// test class already derives the consumer's base usually nests this subclass inside it.
/// </remarks>
/// <typeparam name="TPage">The consumer's routed detail page; it takes the record id as a parameter.</typeparam>
/// <typeparam name="TId">The type of the page's id parameter.</typeparam>
/// <typeparam name="TDto">The record the page's client service returns.</typeparam>
public abstract class DetailPageLoadStateTestsBase<TPage, TId, TDto> : BunitComponentTestBase
    where TPage : class, IComponent
    where TId : notnull
{
    /// <summary>Gets the id the page is opened on.</summary>
    protected abstract TId LoadedId { get; }

    /// <summary>Gets a second id the reader navigates to while the first fetch is still pending.</summary>
    protected abstract TId OtherId { get; }

    /// <summary>Gets the text the page renders for a record that does not exist.</summary>
    protected abstract string NotFoundText { get; }

    /// <summary>Gets the name of the page's route parameter carrying the id. Defaults to <c>Id</c>.</summary>
    protected virtual string IdParameterName => "Id";

    /// <summary>
    /// Gets the accessible label the page gives its loading spinner, or <see langword="null"/> when
    /// it keeps the framework default (the fact then checks only that the busy status renders).
    /// </summary>
    protected virtual string? LoadingLabel => null;

    /// <summary>Gets the alert text for a load that failed. Defaults to the framework's English message.</summary>
    protected virtual string LoadFailedText => "Could not load this page";

    /// <summary>Gets the label of the button that runs the load again. Defaults to the framework's English label.</summary>
    protected virtual string RetryButtonText => "Retry";

    /// <summary>
    /// Gets the not-found failure the client double returns. Override when the page renders the
    /// error's own message rather than a fixed not-found text.
    /// </summary>
    protected virtual Error NotFoundError => Error.NotFoundError("Record.NotFound", "Record not found.");

    /// <summary>Creates the record the client returns for <paramref name="id"/>.</summary>
    /// <param name="id">The id the page fetched.</param>
    /// <returns>A record the page can render.</returns>
    protected abstract TDto CreateRecord(TId id);

    /// <summary>
    /// Gets text that appears in the page's markup only when the record for <paramref name="id"/> is
    /// rendered. The markers for <see cref="LoadedId"/> and <see cref="OtherId"/> must not contain
    /// each other, or the superseded-load fact cannot tell them apart.
    /// </summary>
    /// <param name="id">The id whose record is rendered.</param>
    /// <returns>The marker text.</returns>
    protected abstract string RecordMarker(TId id);

    /// <summary>
    /// Makes the page's client double answer a fetch of <paramref name="id"/> by invoking
    /// <paramref name="fetch"/> on EVERY call (a Moq <c>Returns(Func)</c>, not a fixed value), so a
    /// fact can answer the first fetch and the Retry differently.
    /// </summary>
    /// <param name="id">The id the page fetches.</param>
    /// <param name="fetch">The answer for each call.</param>
    protected abstract void SetupFetch(TId id, Func<Task<Result<TDto>>> fetch);

    [Fact]
    public void WhileTheFetchIsPending_RendersTheLoadingState()
    {
        var pending = new TaskCompletionSource<Result<TDto>>();
        SetupFetch(LoadedId, () => pending.Task);

        var cut = RenderPage(LoadedId);

        cut.FindAll("[role=status][aria-busy=true]").Should().NotBeEmpty();
        if (LoadingLabel is not null)
        {
            cut.Markup.Should().Contain(LoadingLabel);
        }

        // Released so no fetch is left pending past the test.
        pending.SetResult(Result.Failure<TDto>(NotFoundError));
    }

    [Fact]
    public void WhenTheRecordIsNotFound_RendersTheNotFoundState()
    {
        SetupFetch(LoadedId, () => Task.FromResult(Result.Failure<TDto>(NotFoundError)));

        var cut = RenderPage(LoadedId);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(NotFoundText));
    }

    /// <summary>
    /// A failure that is not a 404 is not a missing record: the body says the load failed and
    /// offers Retry, which fetches again (here successfully). A transport failure that read "not
    /// found" would tell the reader not to try again.
    /// </summary>
    [Fact]
    public void WhenTheFetchFails_RendersTheErrorWithRetry_AndRetryFetchesAgain()
    {
        var calls = 0;
        SetupFetch(LoadedId, () =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Result.Failure<TDto>(Error.Failure("Http.Unavailable", "The service is unavailable."))
                : Result.Success(CreateRecord(LoadedId)));
        });

        var cut = RenderPage(LoadedId);

        cut.WaitForAssertion(() => cut.FindAll("[role=alert]")
            .Should().Contain(e => e.TextContent.Contains(LoadFailedText, StringComparison.Ordinal)));
        cut.Markup.Should().NotContain(NotFoundText);

        cut.FindButtonByText(RetryButtonText).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RecordMarker(LoadedId)));
        calls.Should().Be(2);
    }

    /// <summary>
    /// The router reuses the page instance when only the route id changes, so a slow response for
    /// the record the reader left must not land on the page the newer navigation owns.
    /// </summary>
    /// <returns>A task that completes when the fact has run.</returns>
    [Fact]
    public async Task WhenTheRouteIdChangesBeforeTheFirstFetchCompletes_TheStaleResultIsDropped()
    {
        var slow = new TaskCompletionSource<Result<TDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupFetch(LoadedId, () => slow.Task);
        SetupFetch(OtherId, () => Task.FromResult(Result.Success(CreateRecord(OtherId))));

        var cut = RenderPage(LoadedId);
        cut.Render(p => p.TryAdd(IdParameterName, OtherId));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RecordMarker(OtherId))).ConfigureAwait(true);

        await cut.InvokeAsync(() => slow.SetResult(Result.Success(CreateRecord(LoadedId)))).ConfigureAwait(true);
        await DrainDispatcherAsync(cut).ConfigureAwait(true);

        cut.Markup.Should().Contain(RecordMarker(OtherId));
        cut.Markup.Should().NotContain(RecordMarker(LoadedId), "the superseded response must not overwrite the page");
    }

    /// <summary>Renders <typeparamref name="TPage"/> with <paramref name="id"/> as its route parameter.</summary>
    /// <param name="id">The record id.</param>
    /// <returns>The rendered page.</returns>
    protected IRenderedComponent<TPage> RenderPage(TId id)
        => RenderUnderTest<TPage>(p => p.TryAdd(IdParameterName, id));

    // Each await in a page's load path posts one continuation, so a bounded number of round trips is
    // enough for a superseded response to have done whatever it was going to do before the asserts.
    private static async Task DrainDispatcherAsync(IRenderedComponent<TPage> cut)
    {
        for (var i = 0; i < 10; i++)
        {
            await cut.InvokeAsync(() => { }).ConfigureAwait(true);
        }
    }
}
