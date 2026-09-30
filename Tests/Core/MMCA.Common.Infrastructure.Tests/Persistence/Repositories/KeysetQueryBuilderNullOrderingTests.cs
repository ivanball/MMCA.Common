using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Specifications;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Where the null sort keys fall in a keyset page depends on the engine (M112): SQL Server and
/// SQLite sort nulls first ascending and last descending, PostgreSQL the reverse. The seek predicate
/// has to follow the engine's placement, or a PostgreSQL page never reaches the null rows (ascending)
/// or repeats them (descending).
/// </summary>
/// <remarks>
/// The predicate is evaluated by SQLite rather than compiled in memory, because the defect is about
/// SQL's three-valued logic (<c>NULL &lt; 'b'</c> is unknown, whereas a compiled C# string comparison
/// orders null first and answers true). Only the WHERE clause is exercised, so the host engine's own
/// null placement in ORDER BY does not enter the assertion.
/// </remarks>
public sealed class KeysetQueryBuilderNullOrderingTests : IDisposable
{
    private static readonly PropertyInfo CategoryProperty =
        typeof(SpecTestEntity).GetProperty(nameof(SpecTestEntity.Category))!;

    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;

    public KeysetQueryBuilderNullOrderingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _context.Entities.AddRange(
            new SpecTestEntity { Id = 1, Category = null },
            new SpecTestEntity { Id = 2, Category = "b" },
            new SpecTestEntity { Id = 3, Category = "c" },
            new SpecTestEntity { Id = 4, Category = null },
            new SpecTestEntity { Id = 5, Category = "a" });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void AscendingNonNullBoundary_OnPostgreSql_StillReachesTheNullRowsThatSortLast()
    {
        var selected = Select(sortValue: "b", lastId: 2, descending: false, nullsSortFirstAscending: false);

        selected.Should().BeEquivalentTo([3, 1, 4]);
    }

    [Fact]
    public void DescendingNonNullBoundary_OnPostgreSql_DoesNotRepeatTheNullRowsThatSortFirst()
    {
        var selected = Select(sortValue: "b", lastId: 2, descending: true, nullsSortFirstAscending: false);

        selected.Should().BeEquivalentTo([5]);
    }

    [Fact]
    public void AscendingNullBoundary_OnPostgreSql_LeavesOnlyTheLaterNulls()
    {
        var selected = Select(sortValue: null, lastId: 1, descending: false, nullsSortFirstAscending: false);

        selected.Should().BeEquivalentTo([4]);
    }

    [Fact]
    public void SqlServerPlacement_KeepsTheNullsFirstAscendingAndLastDescending()
    {
        Select(sortValue: "b", lastId: 2, descending: false, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([3]);
        Select(sortValue: "b", lastId: 2, descending: true, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([5, 1, 4]);
        Select(sortValue: null, lastId: 1, descending: false, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([2, 3, 4, 5]);
    }

    private int[] Select(object? sortValue, int lastId, bool descending, bool nullsSortFirstAscending)
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<SpecTestEntity, int>(
            CategoryProperty, sortValue, lastId, descending, nullsSortFirstAscending);

        return [.. _context.Entities.AsNoTracking().Where(seek).Select(r => r.Id)];
    }
}
