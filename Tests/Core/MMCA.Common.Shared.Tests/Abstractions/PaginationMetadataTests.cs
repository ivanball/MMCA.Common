using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

public class PaginationMetadataTests
{
    // ── TotalPageCount ──
    [Fact]
    public void TotalPageCount_WithExactDivision_ReturnsCorrectCount()
    {
        var metadata = new PaginationMetadata(100, 10, 1);
        metadata.TotalPageCount.Should().Be(10);
    }

    [Fact]
    public void TotalPageCount_WithRemainder_RoundsUp()
    {
        var metadata = new PaginationMetadata(101, 10, 1);
        metadata.TotalPageCount.Should().Be(11);
    }

    [Fact]
    public void TotalPageCount_WithZeroPageSize_ReturnsZero()
    {
        var metadata = new PaginationMetadata(100, 0, 0);
        metadata.TotalPageCount.Should().Be(0);
    }

    // ── FirstRowOnPage ──
    [Fact]
    public void FirstRowOnPage_FirstPage_ReturnsOne()
    {
        var metadata = new PaginationMetadata(50, 10, 1);
        metadata.FirstRowOnPage.Should().Be(1);
    }

    [Fact]
    public void FirstRowOnPage_SecondPage_ReturnsElevenForPageSizeTen()
    {
        var metadata = new PaginationMetadata(50, 10, 2);
        metadata.FirstRowOnPage.Should().Be(11);
    }

    [Fact]
    public void FirstRowOnPage_WithZeroItems_ReturnsZero()
    {
        var metadata = new PaginationMetadata(0, 10, 1);
        metadata.FirstRowOnPage.Should().Be(0);
    }

    [Fact]
    public void FirstRowOnPage_WithZeroPageSize_ReturnsZero()
    {
        var metadata = new PaginationMetadata(50, 0, 1);
        metadata.FirstRowOnPage.Should().Be(0);
    }

    // ── LastRowOnPage ──
    [Fact]
    public void LastRowOnPage_FullPage_ReturnsPageBoundary()
    {
        var metadata = new PaginationMetadata(50, 10, 1);
        metadata.LastRowOnPage.Should().Be(10);
    }

    [Fact]
    public void LastRowOnPage_PartialLastPage_ReturnsTotalItemCount()
    {
        var metadata = new PaginationMetadata(25, 10, 3);
        metadata.LastRowOnPage.Should().Be(25);
    }

    [Fact]
    public void LastRowOnPage_WithZeroPageSize_ReturnsZero()
    {
        var metadata = new PaginationMetadata(50, 0, 1);
        metadata.LastRowOnPage.Should().Be(0);
    }

    // ── Default constructor ──
    [Fact]
    public void DefaultConstructor_SetsAllToZero()
    {
        var metadata = new PaginationMetadata();
        metadata.TotalItemCount.Should().Be(0);
        metadata.PageSize.Should().Be(0);
        metadata.CurrentPage.Should().Be(0);
    }

    // ── Negative argument validation ──
    [Fact]
    public void Constructor_WithNegativeTotalItemCount_Throws() =>
        FluentActions.Invoking(() => new PaginationMetadata(-1, 10, 1))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Constructor_WithNegativePageSize_Throws() =>
        FluentActions.Invoking(() => new PaginationMetadata(10, -1, 1))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Constructor_WithNegativeCurrentPage_Throws() =>
        FluentActions.Invoking(() => new PaginationMetadata(10, 10, -1))
            .Should().Throw<ArgumentOutOfRangeException>();
}
