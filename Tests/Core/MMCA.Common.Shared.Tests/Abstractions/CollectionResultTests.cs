using FluentAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

public class CollectionResultTests
{
    // ── CollectionResult ──
    [Fact]
    public void CollectionResult_WithItems_StoresItems()
    {
        var items = new List<string> { "a", "b", "c" };
        var result = new CollectionResult<string>(items) { Items = items };

        result.Items.Should().HaveCount(3);
        result.Items.Should().Contain("b");
    }

    [Fact]
    public void CollectionResult_DefaultConstructor_HasEmptyItems()
    {
        var result = new CollectionResult<int> { Items = [] };
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void CollectionResult_WithNullItems_Throws() =>
        FluentActions.Invoking(() => new CollectionResult<string>(null!) { Items = null! })
            .Should().Throw<ArgumentNullException>();

    // ── PagedCollectionResult ──
    [Fact]
    public void PagedCollectionResult_WithItemsAndMetadata_StoresBoth()
    {
        var items = new List<int> { 1, 2, 3 };
        var metadata = new PaginationMetadata(30, 10, 1);

        var result = new PagedCollectionResult<int>(items, metadata)
        {
            Items = items,
            PaginationMetadata = metadata
        };

        result.Items.Should().HaveCount(3);
        result.PaginationMetadata.TotalItemCount.Should().Be(30);
    }

    [Fact]
    public void PagedCollectionResult_DefaultConstructor_HasEmptyItemsAndZeroMetadata()
    {
        var result = new PagedCollectionResult<int>
        {
            Items = [],
            PaginationMetadata = new PaginationMetadata()
        };

        result.Items.Should().BeEmpty();
        result.PaginationMetadata.TotalItemCount.Should().Be(0);
    }

    [Fact]
    public void PagedCollectionResult_WithNullMetadata_Throws() =>
        FluentActions.Invoking(() => new PagedCollectionResult<int>([], null!)
        {
            Items = [],
            PaginationMetadata = null!
        })
            .Should().Throw<ArgumentNullException>();
}
