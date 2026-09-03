using System.Linq.Expressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Specifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories.Read;

/// <summary>
/// Covers keyset ("seek") paging end to end against a real provider: cursor round-trips, next-page
/// detection, the non-unique sort key that makes the identifier tie-break load-bearing, descending
/// pages, nullable sort keys, and the two rejection paths (unknown sort column, malformed cursor).
/// </summary>
public sealed class EFReadRepositoryKeysetPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;
    private readonly EFReadRepository<SpecTestEntity, int> _sut;

    public EFReadRepositoryKeysetPagingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _sut = new EFReadRepository<SpecTestEntity, int>(_context);

        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Six rows whose Name repeats deliberately: without the identifier tie-break a page boundary
    /// inside a repeated name is exactly where a row gets returned twice or never.
    /// </summary>
    private void Seed()
    {
        _context.AddRange(
            new SpecTestEntity { Id = 1, Name = "aaa", Rank = 10, Category = "x" },
            new SpecTestEntity { Id = 2, Name = "bbb", Rank = 20, Category = null },
            new SpecTestEntity { Id = 3, Name = "bbb", Rank = 30, Category = "y" },
            new SpecTestEntity { Id = 4, Name = "bbb", Rank = 40, Category = null },
            new SpecTestEntity { Id = 5, Name = "ccc", Rank = 50, Category = "z" },
            new SpecTestEntity { Id = 6, Name = "ddd", Rank = 60, Category = "z" });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private async Task<List<int>> WalkEveryPageAsync(int pageSize, string? sortColumn, bool descending)
    {
        List<int> ids = [];
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var result = await _sut.GetPageByCursorAsync(
                new KeysetPageRequest(pageSize, sortColumn, descending, cursor));

            result.IsSuccess.Should().BeTrue();
            ids.AddRange(result.Value!.Items.Select(e => e.Id));

            cursor = result.Value.NextCursor;
            if (cursor is null)
                return ids;
        }

        throw new InvalidOperationException("The cursor walk did not terminate.");
    }

    // ── Id-only paging ──
    [Fact]
    public async Task GetPageByCursorAsync_WithNoSortColumn_PagesByIdAscending()
    {
        var ids = await WalkEveryPageAsync(pageSize: 2, sortColumn: null, descending: false);

        ids.Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithNoSortColumnDescending_PagesByIdDescending()
    {
        var ids = await WalkEveryPageAsync(pageSize: 4, sortColumn: null, descending: true);

        ids.Should().Equal(6, 5, 4, 3, 2, 1);
    }

    [Fact]
    public async Task GetPageByCursorAsync_ReturnsTheFirstPageAndACursor()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(e => e.Id).Should().Equal(1, 2);
        result.Value.NextCursor.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPageByCursorAsync_OnTheLastPage_ReturnsNoCursor()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(100));

        result.Value!.Items.Should().HaveCount(6);
        result.Value.NextCursor.Should().BeNull("there is nothing after the last row");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WhenThePageSizeExactlyMatchesTheSet_ReturnsNoCursor()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(6));

        result.Value!.Items.Should().HaveCount(6);
        result.Value.NextCursor.Should().BeNull("the probe row is what proves a next page exists");
    }

    // ── Non-unique sort key: the tie-break is what makes this correct ──
    [Fact]
    public async Task GetPageByCursorAsync_WithARepeatedSortKey_ReturnsEveryRowExactlyOnce()
    {
        var ids = await WalkEveryPageAsync(pageSize: 2, sortColumn: "Name", descending: false);

        ids.Should().Equal(1, 2, 3, 4, 5, 6);
        ids.Should().OnlyHaveUniqueItems("a page boundary inside a repeated sort key must not repeat a row");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithARepeatedSortKeyDescending_ReturnsEveryRowExactlyOnce()
    {
        var ids = await WalkEveryPageAsync(pageSize: 2, sortColumn: "Name", descending: true);

        // Name descending, identifier ascending within each repeated name.
        ids.Should().Equal(6, 5, 2, 3, 4, 1);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithANumericSortKey_Pages()
    {
        var ids = await WalkEveryPageAsync(pageSize: 3, sortColumn: "Rank", descending: false);

        ids.Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task GetPageByCursorAsync_ResolvesTheSortColumnCaseInsensitively()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2, "rank", descending: true));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(e => e.Id).Should().Equal(6, 5);
    }

    // ── Nullable sort keys ──
    [Fact]
    public async Task GetPageByCursorAsync_WithANullableSortKey_ReturnsEveryRowExactlyOnce()
    {
        var ids = await WalkEveryPageAsync(pageSize: 2, sortColumn: "Category", descending: false);

        ids.Should().HaveCount(6).And.OnlyHaveUniqueItems();
        ids.Take(2).Should().BeEquivalentTo([2, 4], "nulls sort first ascending");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithANullableSortKeyDescending_ReturnsEveryRowExactlyOnce()
    {
        var ids = await WalkEveryPageAsync(pageSize: 2, sortColumn: "Category", descending: true);

        ids.Should().HaveCount(6).And.OnlyHaveUniqueItems();
    }

    // ── Specification scoping ──
    [Fact]
    public async Task GetPageByCursorAsync_HonorsTheSpecificationCriteria()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(100), new BbbSpecification());

        result.Value!.Items.Select(e => e.Id).Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithASpecification_KeepsScopingEveryPage()
    {
        var first = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2), new BbbSpecification());
        var second = await _sut.GetPageByCursorAsync(
            new KeysetPageRequest(2, cursor: first.Value!.NextCursor), new BbbSpecification());

        first.Value.Items.Select(e => e.Id).Should().Equal(2, 3);
        second.Value!.Items.Select(e => e.Id).Should().Equal(4);
        second.Value.NextCursor.Should().BeNull();
    }

    // ── Rejections ──
    [Fact]
    public async Task GetPageByCursorAsync_WithAnUnknownSortColumn_FailsValidation()
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2, "NotAColumn"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
        result.Errors[0].Code.Should().Be("Error.InvalidEntityField");
    }

    [Theory]
    [InlineData("not-a-cursor!!")]
    [InlineData("Zm9v")]
    public async Task GetPageByCursorAsync_WithAMalformedCursor_FailsValidation(string cursor)
    {
        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2, cursor: cursor));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Error.InvalidCursor");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithACursorWhoseIdIsNotTheKeyType_FailsValidation()
    {
        var forged = KeysetCursor.Encode(null, "not-an-int");

        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2, cursor: forged));

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Error.InvalidCursor");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithACursorWhoseSortValueIsNotTheKeyType_FailsValidation()
    {
        var forged = KeysetCursor.Encode("not-a-number", "1");

        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(2, "Rank", cursor: forged));

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Error.InvalidCursor");
    }

    [Fact]
    public async Task GetPageByCursorAsync_WithNullRequest_Throws()
    {
        var act = () => _sut.GetPageByCursorAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Soft delete ──
    [Fact]
    public async Task GetPageByCursorAsync_ExcludesSoftDeletedRows()
    {
        var deleted = await _context.Entities.SingleAsync(e => e.Id == 3);
        deleted.Delete().IsSuccess.Should().BeTrue();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _sut.GetPageByCursorAsync(new KeysetPageRequest(100));

        result.Value!.Items.Select(e => e.Id).Should().Equal(1, 2, 4, 5, 6);
    }

    private sealed class BbbSpecification : Specification<SpecTestEntity, int>
    {
        public override Expression<Func<SpecTestEntity, bool>> Criteria => e => e.Name == "bbb";
    }
}
