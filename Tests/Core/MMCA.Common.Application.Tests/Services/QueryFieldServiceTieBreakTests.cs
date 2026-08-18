using AwesomeAssertions;
using MMCA.Common.Application.Services;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// Direct coverage of the tie-break parameter added to <c>QueryFieldService.ApplySorting</c>: it
/// turns a partial order into a total one, which is what makes Skip/Take repeatable. Omitting it
/// must leave the previous behaviour exactly as it was.
/// </summary>
public sealed class QueryFieldServiceTieBreakTests
{
    private sealed class SortTestEntity
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private static readonly IReadOnlyDictionary<string, string> NoMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IQueryable<SortTestEntity> Rows =>
        new List<SortTestEntity>
        {
            new() { Id = 3, Name = "b" },
            new() { Id = 1, Name = "b" },
            new() { Id = 4, Name = "a" },
            new() { Id = 2, Name = "a" },
        }.AsQueryable();

    [Fact]
    public void ApplySorting_WithATieBreakAndNoSortColumn_OrdersByTheTieBreak()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, null, null, NoMap, tieBreakProperty: "Id");

        sorted.Select(e => e.Id).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void ApplySorting_WithATieBreak_AppendsItAfterTheRequestedSort()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, "Name", "asc", NoMap, tieBreakProperty: "Id");

        sorted.Select(e => e.Id).Should().Equal(2, 4, 1, 3);
    }

    [Fact]
    public void ApplySorting_WithATieBreakAndADescendingSort_KeepsTheTieBreakAscending()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, "Name", "desc", NoMap, tieBreakProperty: "Id");

        sorted.Select(e => e.Id).Should().Equal(1, 3, 2, 4);
    }

    [Fact]
    public void ApplySorting_WhenTheSortColumnIsTheTieBreak_DoesNotRepeatTheKey()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, "Id", "desc", NoMap, tieBreakProperty: "Id");

        sorted.Select(e => e.Id).Should().Equal(4, 3, 2, 1);
    }

    [Fact]
    public void ApplySorting_WithADefaultSortAndATieBreak_AppliesBoth()
    {
        var sorted = QueryFieldService.ApplySorting(
            Rows,
            "NotAColumn",
            "asc",
            NoMap,
            defaultSort: e => e.Name,
            tieBreakProperty: "Id");

        sorted.Select(e => e.Id).Should().Equal(2, 4, 1, 3);
    }

    [Fact]
    public void ApplySorting_WithoutATieBreak_LeavesAnUnsortedQueryUnsorted()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, null, null, NoMap);

        sorted.Select(e => e.Id).Should().Equal(3, 1, 4, 2);
    }

    [Fact]
    public void ApplySorting_WithoutATieBreak_SortsByTheRequestedColumnAlone()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, "Name", "asc", NoMap);

        sorted.Select(e => e.Id).Should().Equal(4, 2, 3, 1);
    }

    [Fact]
    public void ApplySorting_IgnoresABlankTieBreak()
    {
        var sorted = QueryFieldService.ApplySorting(Rows, null, null, NoMap, tieBreakProperty: "   ");

        sorted.Select(e => e.Id).Should().Equal(3, 1, 4, 2);
    }
}
