using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// Pins the deterministic-ordering guarantee: a PAGINATED read always ends up with a total order,
/// because <c>Skip</c>/<c>Take</c> over a partial order is undefined and lets the same row appear on
/// two consecutive pages while another appears on none. An unpaginated read is deliberately left
/// alone, since it materializes one capped set in one statement and would otherwise pay for a sort
/// nobody asked for.
/// </summary>
public sealed class EntityQueryPipelineOrderingTests
{
    private readonly EntityQueryPipeline _sut = new(new InMemoryQueryableExecutor());

    private sealed class OrderingTestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>Four rows sharing two names, deliberately seeded out of identifier order.</summary>
    private static IQueryable<OrderingTestEntity> Rows =>
        new List<OrderingTestEntity>
        {
            new() { Id = 3, Name = "b" },
            new() { Id = 1, Name = "b" },
            new() { Id = 4, Name = "a" },
            new() { Id = 2, Name = "a" },
        }.AsQueryable();

    private static EntityQueryParameters<OrderingTestEntity> Parameters(
        string? sortColumn = null,
        string? sortDirection = null,
        int? pageNumber = null,
        int? pageSize = null)
        => new()
        {
            SortColumn = sortColumn,
            SortDirection = sortDirection,
            PageNumber = pageNumber,
            PageSize = pageSize,
            DTOToEntityPropertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

    private Task<(IReadOnlyCollection<OrderingTestEntity> Items, int TotalCount)> ExecuteAsync(
        EntityQueryParameters<OrderingTestEntity> parameters)
        => _sut.ExecuteAsync<OrderingTestEntity, int>(
            Rows,
            new NavigationMetadata(),
            parameters,
            (_, _, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

    // ── Paginated reads get a total order ──
    [Fact]
    public async Task PaginatedRead_WithNoSortColumn_OrdersByIdAscending()
    {
        var (items, _) = await ExecuteAsync(Parameters(pageNumber: 1, pageSize: 4));

        items.Select(e => e.Id).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task PaginatedRead_WithANonUniqueSort_AppendsTheIdTieBreak()
    {
        var (items, _) = await ExecuteAsync(Parameters("Name", "asc", pageNumber: 1, pageSize: 4));

        items.Select(e => e.Id).Should().Equal(
            [2, 4, 1, 3],
            "rows sharing a sort value must be ordered by the key, otherwise the page boundary is arbitrary");
    }

    [Fact]
    public async Task PaginatedRead_WithADescendingSort_StillTieBreaksAscendingById()
    {
        var (items, _) = await ExecuteAsync(Parameters("Name", "desc", pageNumber: 1, pageSize: 4));

        items.Select(e => e.Id).Should().Equal(1, 3, 2, 4);
    }

    [Fact]
    public async Task ConsecutivePages_ReturnEveryRowExactlyOnce()
    {
        var (first, _) = await ExecuteAsync(Parameters("Name", "asc", pageNumber: 1, pageSize: 2));
        var (second, _) = await ExecuteAsync(Parameters("Name", "asc", pageNumber: 2, pageSize: 2));

        var ids = first.Concat(second).Select(e => e.Id).ToList();

        ids.Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [Fact]
    public async Task PaginatedRead_SortingByIdItself_DoesNotRepeatTheKey()
    {
        var (items, _) = await ExecuteAsync(Parameters("Id", "desc", pageNumber: 1, pageSize: 4));

        items.Select(e => e.Id).Should().Equal([4, 3, 2, 1], "the tie-break must not fight the requested sort");
    }

    // ── Unpaginated reads keep their previous behaviour ──
    [Fact]
    public async Task UnpaginatedRead_WithNoSortColumn_IsLeftUnordered()
    {
        var (items, _) = await ExecuteAsync(Parameters());

        items.Select(e => e.Id).Should().Equal([3, 1, 4, 2], "an unpaginated read must not pay for a sort");
    }

    [Fact]
    public async Task UnpaginatedRead_WithASortColumn_SortsByItAlone()
    {
        var (items, _) = await ExecuteAsync(Parameters("Name", "asc"));

        // Stable LINQ-to-Objects ordering keeps the seeded order inside each name group.
        items.Select(e => e.Id).Should().Equal(4, 2, 3, 1);
    }

    /// <summary>
    /// Executes the queryable for real (LINQ to Objects) so the tests observe the ORDER the pipeline
    /// actually produced, which a Moq executor returning a canned list cannot show.
    /// </summary>
    private sealed class InMemoryQueryableExecutor : IQueryableExecutor
    {
        public IQueryable<T> Include<T>(IQueryable<T> query, string navigationPropertyPath)
            where T : class => query;

        public IQueryable<T> AsSplitQuery<T>(IQueryable<T> query)
            where T : class => query;

        public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
            => Task.FromResult(query.ToList());

        public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
            => Task.FromResult(query.Count());
    }
}
