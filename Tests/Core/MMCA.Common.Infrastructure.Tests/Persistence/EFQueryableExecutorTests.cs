using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="EFQueryableExecutor"/> verifying in-memory (non-EF) fallback behavior.
/// When the queryable is a plain LINQ-to-Objects collection (not IAsyncEnumerable),
/// the executor falls back to synchronous evaluation.
/// </summary>
public sealed class EFQueryableExecutorTests
{
    private readonly EFQueryableExecutor _sut = new();

    private sealed record TestItem(int Id, string Name);

    // ── Include ──
    [Fact]
    public void Include_WithInMemoryQueryable_ReturnsQueryUnchanged()
    {
        var items = new List<TestItem> { new(1, "A"), new(2, "B") };
        var query = items.AsQueryable();

        var result = _sut.Include(query, "Name");

        result.Should().BeSameAs(query);
    }

    // ── ToListAsync ──
    [Fact]
    public async Task ToListAsync_WithInMemoryQueryable_ReturnsAllItems()
    {
        var items = new List<TestItem> { new(1, "A"), new(2, "B"), new(3, "C") };
        var query = items.AsQueryable();

        var result = await _sut.ToListAsync(query, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().BeEquivalentTo(items);
    }

    [Fact]
    public async Task ToListAsync_WithEmptyQueryable_ReturnsEmptyList()
    {
        var query = Array.Empty<TestItem>().AsQueryable();

        var result = await _sut.ToListAsync(query, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ToListAsync_WithFilteredQueryable_ReturnsFilteredResults()
    {
        var items = new List<TestItem> { new(1, "Alpha"), new(2, "Beta"), new(3, "Alpha") };
        var query = items.AsQueryable().Where(i => i.Name == "Alpha");

        var result = await _sut.ToListAsync(query, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(item => item.Name.Should().Be("Alpha"));
    }

    // ── CountAsync ──
    [Fact]
    public async Task CountAsync_WithInMemoryQueryable_ReturnsTotalCount()
    {
        var items = new List<TestItem> { new(1, "A"), new(2, "B"), new(3, "C") };
        var query = items.AsQueryable();

        var count = await _sut.CountAsync(query, CancellationToken.None);

        count.Should().Be(3);
    }

    [Fact]
    public async Task CountAsync_WithEmptyQueryable_ReturnsZero()
    {
        var query = Array.Empty<TestItem>().AsQueryable();

        var count = await _sut.CountAsync(query, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task CountAsync_WithFilteredQueryable_ReturnsFilteredCount()
    {
        var items = new List<TestItem> { new(1, "X"), new(2, "Y"), new(3, "X") };
        var query = items.AsQueryable().Where(i => i.Name == "X");

        var count = await _sut.CountAsync(query, CancellationToken.None);

        count.Should().Be(2);
    }
}
