using AwesomeAssertions;
using MMCA.Common.Application.Services.Query;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// Covers the shared Skip/Take clamp. The interesting cases are the ones a 32-bit
/// <c>(pageNumber - 1) * pageSize</c> gets wrong: a page number near <see cref="int.MaxValue"/>
/// wraps NEGATIVE, and a negative OFFSET is rejected by SQL Server rather than ignored, so the
/// caller sees a 500 where the honest answer is an empty page.
/// </summary>
public class PagingMathTests
{
    [Fact]
    public void Clamp_FirstPage_SkipsNothing()
    {
        var (skip, take) = PagingMath.Clamp(pageNumber: 1, pageSize: 20, maxPageSize: 500);

        skip.Should().Be(0);
        take.Should().Be(20);
    }

    [Fact]
    public void Clamp_MiddlePage_SkipsWholePages()
    {
        var (skip, take) = PagingMath.Clamp(pageNumber: 4, pageSize: 25, maxPageSize: 500);

        skip.Should().Be(75);
        take.Should().Be(25);
    }

    [Fact]
    public void Clamp_PageSizeAboveCeiling_ClampsToCeiling()
    {
        var (_, take) = PagingMath.Clamp(pageNumber: 1, pageSize: 100_000, maxPageSize: 500);

        take.Should().Be(500);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_NonPositivePageSize_FloorsAtOne(int pageSize)
    {
        var (skip, take) = PagingMath.Clamp(pageNumber: 3, pageSize: pageSize, maxPageSize: 500);

        take.Should().Be(1, "a zero or negative Take is not a valid query");
        skip.Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void Clamp_NonPositivePageNumber_TreatedAsFirstPage(int pageNumber)
    {
        var (skip, _) = PagingMath.Clamp(pageNumber, pageSize: 20, maxPageSize: 500);

        skip.Should().Be(0, "a negative Skip is rejected outright by SQL Server, not treated as zero");
    }

    [Theory]
    [InlineData(int.MaxValue, 500)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MaxValue - 1, 500)]
    [InlineData(5_000_000, 500)]
    public void Clamp_PageBeyondReachableOffset_YieldsAnEmptyPageRatherThanOverflowing(int pageNumber, int pageSize)
    {
        var (skip, take) = PagingMath.Clamp(pageNumber, pageSize, maxPageSize: 500);

        skip.Should().BeGreaterThanOrEqualTo(0, "32-bit multiplication wrapped this negative");
        (skip, take).Should().Be((0, 0), "a page past the reachable offset range genuinely holds nothing");
    }

    [Fact]
    public void Clamp_LargestNonOverflowingPage_StillPaginates()
    {
        // Straddles the boundary: this offset fits in an int, so it must page normally rather than
        // being lumped in with the out-of-range case above.
        var (skip, take) = PagingMath.Clamp(pageNumber: 4_000_000, pageSize: 500, maxPageSize: 500);

        skip.Should().Be(1_999_999_500);
        take.Should().Be(500);
    }
}
