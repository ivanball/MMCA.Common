using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components.Lists;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for a consumer's list page: a <c>DataGridListPageBase</c> page rendered through the
/// framework's <see cref="ListPageShell{T}"/> (ADR-146). The framework tests the base and the shell;
/// what is pinned here is each page's own wiring of them: its title reaches the shell header, an
/// empty page renders the page's empty-state text, a failed fetch renders the Retry state instead of
/// claiming there are no records, and the first column links to the record's detail route (or, for
/// a page that declares no row link, renders none).
/// </summary>
/// <remarks>
/// A consumer keeps a small sealed subclass naming the page, its rows, and how its client double
/// answers a page fetch (the ADR-015 pattern). The subclass constructor registers the page's
/// services, including whatever the consumer's own bUnit base would have registered, since this
/// class derives the framework base directly. The data-grid host is configured on the first
/// <see cref="RenderList"/>, after every constructor registration, because configuring it freezes
/// the service provider.
/// </remarks>
/// <typeparam name="TPage">The consumer's routed list page.</typeparam>
/// <typeparam name="TDto">The row type the page's client service pages through.</typeparam>
public abstract class DataGridListPageTestsBase<TPage, TDto> : BunitComponentTestBase
    where TPage : class, IComponent
{
    private bool _hostConfigured;

    /// <summary>Gets the title the page passes to the list shell.</summary>
    protected abstract string PageTitle { get; }

    /// <summary>Gets the text the page renders when the fetch succeeds with no rows.</summary>
    protected abstract string EmptyStateText { get; }

    /// <summary>
    /// Gets text that appears in the markup only once the rows from <see cref="CreateRows"/> have
    /// rendered (for example the first row's name).
    /// </summary>
    protected abstract string FirstRowMarker { get; }

    /// <summary>
    /// Gets the detail route the first row's <see cref="RowLinkCell"/> links to, or
    /// <see langword="null"/> for a page whose rows link nowhere (the fact then asserts no row link renders).
    /// </summary>
    protected virtual string? FirstRowHref => null;

    /// <summary>Gets the alert text for a fetch that failed. Defaults to the framework's English message.</summary>
    protected virtual string LoadFailedText => "Could not load this list";

    /// <summary>Creates the rows a successful fetch returns; the first row is the one the row-link fact reads.</summary>
    /// <returns>At least one row.</returns>
    protected abstract IReadOnlyList<TDto> CreateRows();

    /// <summary>
    /// Makes the page's client double answer every page fetch by invoking <paramref name="fetch"/>
    /// (a Moq <c>Returns(Func)</c> over any filter, page, sort and cache arguments).
    /// </summary>
    /// <param name="fetch">The answer for each call.</param>
    protected abstract void SetupFetch(Func<Task<Result<(IReadOnlyList<TDto> Items, int TotalItems)>>> fetch);

    [Fact]
    public void ThePageTitle_ReachesTheListShell()
    {
        SetupRows(CreateRows());

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.FindComponents<ListPageShell<TDto>>()
            .Should().ContainSingle()
            .Which.Instance.Title.Should().Be(PageTitle));
    }

    [Fact]
    public void WithNoRows_RendersTheEmptyState()
    {
        SetupRows([]);

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(EmptyStateText));
    }

    /// <summary>
    /// The base unwraps the failed result and hands the grid an empty page, so without the failure
    /// slot a transport fault would read as "there are no records".
    /// </summary>
    [Fact]
    public void WhenTheFetchFails_RendersTheRetryState_NotTheEmptyState()
    {
        SetupFetch(() => Task.FromResult(Result.Failure<(IReadOnlyList<TDto> Items, int TotalItems)>(
            Error.Unexpected("Http.Failure", "The service is unavailable."))));

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.FindAll("[role=alert]")
            .Should().Contain(e => e.TextContent.Contains(LoadFailedText, StringComparison.Ordinal)));
        cut.Markup.Should().NotContain(EmptyStateText);
    }

    [Fact]
    public void TheFirstRow_LinksToItsDetailRoute()
    {
        SetupRows(CreateRows());

        var cut = RenderList();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(FirstRowMarker));

        if (FirstRowHref is null)
        {
            cut.FindComponents<RowLinkCell>().Should().BeEmpty();
            return;
        }

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindComponents<RowLinkCell>();
            cells.Should().NotBeEmpty();
            cells[0].Instance.Href.Should().Be(FirstRowHref);
        });
    }

    /// <summary>Makes every fetch succeed with <paramref name="rows"/> as the whole result.</summary>
    /// <param name="rows">The rows to return.</param>
    protected void SetupRows(IReadOnlyList<TDto> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SetupFetch(() => Task.FromResult(Result.Success<(IReadOnlyList<TDto> Items, int TotalItems)>((rows, rows.Count))));
    }

    /// <summary>
    /// Configures the data-grid host on first use, renders the MudBlazor providers, and renders
    /// <typeparamref name="TPage"/> with no parameters.
    /// </summary>
    /// <returns>The rendered page.</returns>
    protected IRenderedComponent<TPage> RenderList()
    {
        if (!_hostConfigured)
        {
            ConfigureDataGridListPageHost();
            _hostConfigured = true;
        }

        RenderMudProviders();
        return RenderUnderTest<TPage>(_ => { });
    }
}
