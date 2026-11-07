using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="SupportAccessLog"/> component (ADR-137): one row per grant
/// with both parties, the full reason and a status chip that carries its text, the pager over the
/// server's total, the no-snapshot placeholder, the filters reaching the service, and Spanish.
/// </summary>
public sealed class SupportAccessLogTests : BunitTestBase
{
    private const string LongReason = "Ticket 4411: the attendee reports that the schedule page shows no sessions after the agenda import on Friday";

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IViewAsGrantsUIService> _grants = new();

    public SupportAccessLogTests()
    {
        Services.AddSingleton(_grants.Object);
        _grants.Setup(g => g.GetPageAsync(It.IsAny<ViewAsGrantListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ViewAsGrantListRequest request, CancellationToken _) => Page(request.PageNumber, request.PageSize, total: 3,
                Row("ana@staff.test", "bob@example.com", ViewAsGrantStatus.Live, LongReason),
                Row("dan@staff.test", "carla@example.com", ViewAsGrantStatus.Ended, "Ticket 4412: badge printed twice")));
    }

    // Spec A assertion 4: the rows carry both names, the reason in full and a status chip with text.
    [Fact]
    public async Task RendersOneRowPerGrant_WithBothNames_TheFullReason_AndAStatusChip()
    {
        var cut = RenderLog();

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.FindAll("[data-testid=\"grant-staff\"]").Select(c => c.TextContent.Trim()).Should().Equal("ana@staff.test", "dan@staff.test");
        cut.FindAll("[data-testid=\"grant-viewed\"]").Select(c => c.TextContent.Trim()).Should().Equal("bob@example.com", "carla@example.com");
        cut.FindAll("[data-testid=\"grant-reason\"]")[0].TextContent.Trim().Should().Be(LongReason, "the reason is shown in full, never truncated");
        var chips = cut.FindAll("[data-testid=\"grant-status\"] .mud-chip");
        chips.Select(c => c.TextContent.Trim()).Should().Equal(["Live", "Ended"], "the status is text, not colour alone");
    }

    // Spec A assertion 4 (paging): the pager pages the server's total and reads the chosen page.
    [Fact]
    public async Task ThePager_ReadsTheChosenPageFromTheServer()
    {
        var cut = RenderLog();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.FindAll(".mud-pagination button").Should().Contain(b => b.TextContent.Trim() == "2", "three grants at two per page make two pages");
        await cut.FindAll(".mud-pagination button").Single(b => b.TextContent.Trim() == "2").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _grants.Verify(
            g => g.GetPageAsync(It.Is<ViewAsGrantListRequest>(r => r.PageNumber == 2 && r.PageSize == 2), It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Fact]
    public async Task APartyWithNoSnapshot_ShowsThePlaceholderInsteadOfABlankCell()
    {
        _grants.Setup(g => g.GetPageAsync(It.IsAny<ViewAsGrantListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(1, 2, total: 1, Row(null, "bob@example.com", ViewAsGrantStatus.Expired, LongReason)));

        var cut = RenderLog();

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.Find("[data-testid=\"grant-staff\"]").TextContent.Trim().Should().Be("Email not on record", "an erased account's snapshot is blanked");
        cut.Find("[data-testid=\"grant-status\"]").TextContent.Trim().Should().Be("Expired");
    }

    [Fact]
    public async Task ApplyingTheFilters_ReadsTheFirstPageWithThem()
    {
        var cut = RenderLog();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        await cut.Find("[data-testid=\"filter-staff\"] input").InputAsync(new ChangeEventArgs { Value = "ana" });
        await cut.Find("[data-testid=\"filter-viewed\"] input").InputAsync(new ChangeEventArgs { Value = "bob" });
        await cut.Find("[data-testid=\"apply-filters\"]").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _grants.Verify(
            g => g.GetPageAsync(
                It.Is<ViewAsGrantListRequest>(r => r.Staff == "ana" && r.Viewed == "bob" && r.PageNumber == 1),
                It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Fact]
    public Task WithNoGrants_RendersTheEmptyState()
    {
        _grants.Setup(g => g.GetPageAsync(It.IsAny<ViewAsGrantListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(1, 2, total: 0));

        var cut = RenderLog();

        return cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("No support access matches these filters."));
    }

    [Fact]
    public async Task WhenTheLoadFails_RendersTheInlineFailureAndARetry()
    {
        _grants.Setup(g => g.GetPageAsync(It.IsAny<ViewAsGrantListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<ViewAsGrantSummaryResponse>>(
                Error.Failure("Admin.ViewAsGrants.LoadFailed", "The support access log could not be loaded.")));

        var cut = RenderLog();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("The support access log could not be loaded."));
        cut.Markup.Should().Contain("Try again");
        cut.Markup.Should().NotContain("No support access matches these filters.");
    }

    [Fact]
    public async Task InSpanish_TheHeadingsAndStatusAreTranslated()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es");
        try
        {
            var cut = RenderLog();

            await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));
            cut.Markup.Should().Contain("Registro de acceso de soporte");
            cut.FindAll("[data-testid=\"grant-status\"] .mud-chip").Select(c => c.TextContent.Trim()).Should().Equal("Activo", "Finalizado");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static ViewAsGrantSummaryResponse Row(string? staff, string? viewed, ViewAsGrantStatus status, string reason) =>
        new(Guid.NewGuid(), 1, staff, 10, viewed, reason, Now.AddMinutes(-30), Now.AddMinutes(-15), status == ViewAsGrantStatus.Ended ? Now.AddMinutes(-20) : null, status);

    private static Result<PagedCollectionResult<ViewAsGrantSummaryResponse>> Page(int page, int pageSize, int total, params ViewAsGrantSummaryResponse[] rows) =>
        Result.Success(new PagedCollectionResult<ViewAsGrantSummaryResponse>(rows, new PaginationMetadata(total, pageSize, page)));

    private IRenderedComponent<SupportAccessLog> RenderLog()
    {
        // The filter row's status select and date pickers need a popover host.
        RenderMudProviders();
        return RenderUnderTest<SupportAccessLog>(p => p.Add(x => x.PageSize, 2));
    }
}
