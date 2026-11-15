using System.Linq.Expressions;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Keyset paging over an ENUM sort property. An enum has no relational operator of its own in an
/// expression tree (<c>Expression.GreaterThan</c> is defined for the numeric types only), so the seek
/// predicate must compare it by its underlying numeric value. These tests page a non-nullable and a
/// nullable <see cref="DayOfWeek"/> key in both directions and assert the rows the predicate selects:
/// the order is the enum's NUMERIC order (Sunday = 0 .. Saturday = 6, deliberately not the
/// alphabetical order of the names), then the identifier tie-break.
/// </summary>
/// <remarks>
/// The same eight rows are evaluated twice: once through the compiled predicate in memory, and once
/// through EF Core on SQLite, which proves the predicate also translates. The nullable cases follow
/// the engine-dependent null placement the sibling <c>KeysetQueryBuilderNullOrderingTests</c> pins
/// for a string key (SQL Server / SQLite: nulls first ascending; PostgreSQL: nulls last ascending).
/// The control case runs the identical expectations over an <see cref="int"/> key holding the same
/// numbers, so a failure here is about the enum and nothing else.
/// </remarks>
public sealed class KeysetQueryBuilderEnumSortTests : IDisposable
{
    private static readonly PropertyInfo DayProperty =
        typeof(KeysetEnumEntity).GetProperty(nameof(KeysetEnumEntity.Day))!;

    private static readonly PropertyInfo OptionalDayProperty =
        typeof(KeysetEnumEntity).GetProperty(nameof(KeysetEnumEntity.OptionalDay))!;

    private static readonly PropertyInfo DayNumberProperty =
        typeof(KeysetEnumEntity).GetProperty(nameof(KeysetEnumEntity.DayNumber))!;

    private readonly SqliteConnection _connection;
    private readonly KeysetEnumTestDbContext _context;

    public KeysetQueryBuilderEnumSortTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new KeysetEnumTestDbContext(
            new DbContextOptionsBuilder<KeysetEnumTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _context.Entities.AddRange(CreateRows());
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // Control: an int key with the same numbers already pages correctly
    [Fact]
    public void Control_IntKeyWithTheSameNumbers_SelectsTheExpectedRowsInBothDirections()
    {
        SelectInMemory(DayNumberProperty, (int)DayOfWeek.Wednesday, lastId: 2, descending: false)
            .Should().BeEquivalentTo([4, 6, 7, 8]);
        SelectInMemory(DayNumberProperty, (int)DayOfWeek.Wednesday, lastId: 2, descending: true)
            .Should().BeEquivalentTo([1, 3, 5, 6, 8]);
    }

    // Non-nullable enum key, compiled in memory
    [Fact]
    public void NonNullableEnumKey_Ascending_SelectsTheLaterDaysThenTheLaterTies()
    {
        var selected = SelectInMemory(DayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false);

        selected.Should().BeEquivalentTo(
            [4, 6, 7, 8],
            "after (Wednesday, 2) come Friday (4), Saturday (7) and the Wednesday ties with a later id (6, 8); "
            + "alphabetical order would have selected only the ties");
    }

    [Fact]
    public void NonNullableEnumKey_Descending_SelectsTheEarlierDaysThenTheLaterTies()
    {
        var selected = SelectInMemory(DayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true);

        selected.Should().BeEquivalentTo(
            [1, 3, 5, 6, 8],
            "walking backwards from (Wednesday, 2) reaches Monday (1, 3), Sunday (5) and the Wednesday ties "
            + "with a later id (6, 8), because the identifier tie-break always ascends");
    }

    // Nullable enum key, compiled in memory
    [Fact]
    public void NullableEnumKey_SqlServerPlacement_FollowsTheNumericOrderAndPutsTheNullsFirstAscending()
    {
        SelectInMemory(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([3, 6, 7], "the nulls sort first ascending, so they are all behind a non-null boundary");
        SelectInMemory(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([1, 4, 5, 6, 8], "the nulls sort last descending, so they are all still ahead");
        SelectInMemory(OptionalDayProperty, sortValue: null, lastId: 1, descending: false, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([2, 3, 4, 5, 6, 7, 8], "after a null boundary come the later nulls and every non-null key");
    }

    [Fact]
    public void NullableEnumKey_PostgreSqlPlacement_FollowsTheNumericOrderAndPutsTheNullsLastAscending()
    {
        SelectInMemory(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false, nullsSortFirstAscending: false)
            .Should().BeEquivalentTo([1, 3, 4, 6, 7], "the nulls sort last ascending, so they are all still ahead");
        SelectInMemory(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true, nullsSortFirstAscending: false)
            .Should().BeEquivalentTo([5, 6, 8], "the nulls sort first descending, so they are all behind a non-null boundary");
        SelectInMemory(OptionalDayProperty, sortValue: null, lastId: 1, descending: false, nullsSortFirstAscending: false)
            .Should().BeEquivalentTo([4], "after a null boundary that sorts last only the later nulls remain");
    }

    // The same predicates through EF Core on SQLite
    [Fact]
    public void NonNullableEnumKey_OnSqlite_TranslatesAndPagesInNumericOrder()
    {
        SelectOrderedPageOnSqlite(DayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false)
            .Should().Equal(
                [6, 8, 4, 7],
                "the page continues with the Wednesday ties by id, then Friday, then Saturday");
        SelectOrderedPageOnSqlite(DayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true)
            .Should().Equal(
                [6, 8, 1, 3, 5],
                "the page continues with the Wednesday ties by id, then the Mondays by id, then Sunday");
    }

    [Fact]
    public void NullableEnumKey_OnSqlite_TranslatesAndHonoursTheNullPlacement()
    {
        SelectOnSqlite(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([3, 6, 7]);
        SelectOnSqlite(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true, nullsSortFirstAscending: true)
            .Should().BeEquivalentTo([1, 4, 5, 6, 8]);
        SelectOnSqlite(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: false, nullsSortFirstAscending: false)
            .Should().BeEquivalentTo([1, 3, 4, 6, 7]);
        SelectOnSqlite(OptionalDayProperty, DayOfWeek.Wednesday, lastId: 2, descending: true, nullsSortFirstAscending: false)
            .Should().BeEquivalentTo([5, 6, 8]);
    }

    /// <summary>
    /// The fixture. <c>Day</c> and <c>OptionalDay</c> are the enum keys; <c>DayNumber</c> carries the
    /// same numbers as <c>Day</c> for the control case. The days are chosen so the numeric and the
    /// alphabetical orders disagree; <c>Day</c> carries Wednesday ties on ids 2, 6 and 8, and
    /// <c>OptionalDay</c> on ids 2 and 6, with two null rows (1 and 4).
    /// </summary>
    /// <returns>The rows, freshly allocated so EF and the in-memory evaluation never share instances.</returns>
    private static KeysetEnumEntity[] CreateRows() =>
    [
        Row(1, DayOfWeek.Monday, optionalDay: null),
        Row(2, DayOfWeek.Wednesday, DayOfWeek.Wednesday),
        Row(3, DayOfWeek.Monday, DayOfWeek.Friday),
        Row(4, DayOfWeek.Friday, optionalDay: null),
        Row(5, DayOfWeek.Sunday, DayOfWeek.Monday),
        Row(6, DayOfWeek.Wednesday, DayOfWeek.Wednesday),
        Row(7, DayOfWeek.Saturday, DayOfWeek.Saturday),
        Row(8, DayOfWeek.Wednesday, DayOfWeek.Sunday),
    ];

    private static KeysetEnumEntity Row(int id, DayOfWeek day, DayOfWeek? optionalDay) =>
        new() { Id = id, Day = day, OptionalDay = optionalDay, DayNumber = (int)day };

    private static Expression<Func<KeysetEnumEntity, bool>> Seek(
        PropertyInfo property,
        object? sortValue,
        int lastId,
        bool descending,
        bool nullsSortFirstAscending) =>
        KeysetQueryBuilder.BuildSeekPredicate<KeysetEnumEntity, int>(
            property, sortValue, lastId, descending, nullsSortFirstAscending);

    private static int[] SelectInMemory(
        PropertyInfo property,
        object? sortValue,
        int lastId,
        bool descending,
        bool nullsSortFirstAscending = true)
    {
        var predicate = Seek(property, sortValue, lastId, descending, nullsSortFirstAscending).Compile();

        return [.. CreateRows().Where(predicate).Select(r => r.Id)];
    }

    private int[] SelectOnSqlite(
        PropertyInfo property,
        object? sortValue,
        int lastId,
        bool descending,
        bool nullsSortFirstAscending)
    {
        var seek = Seek(property, sortValue, lastId, descending, nullsSortFirstAscending);

        return [.. _context.Entities.AsNoTracking().Where(seek).Select(r => r.Id)];
    }

    private int[] SelectOrderedPageOnSqlite(PropertyInfo property, object sortValue, int lastId, bool descending)
    {
        var seek = Seek(property, sortValue, lastId, descending, nullsSortFirstAscending: true);
        var page = KeysetQueryBuilder.ApplyOrdering<KeysetEnumEntity, int>(
            _context.Entities.AsNoTracking().Where(seek), property, descending);

        return [.. page.Select(r => r.Id)];
    }
}

/// <summary>An entity whose keyset sort keys are a non-nullable and a nullable enum.</summary>
public sealed class KeysetEnumEntity : AuditableBaseEntity<int>
{
    /// <summary>Gets or sets the non-nullable enum sort key.</summary>
    public DayOfWeek Day { get; set; }

    /// <summary>Gets or sets the nullable enum sort key.</summary>
    public DayOfWeek? OptionalDay { get; set; }

    /// <summary>Gets or sets the numeric value of <see cref="Day"/>, the control case's int key.</summary>
    public int DayNumber { get; set; }
}

/// <summary>
/// A minimal SQLite-mappable context over <see cref="KeysetEnumEntity"/>, declared the same way as
/// the specification tests' context. The enums map to their default integer storage.
/// </summary>
public sealed class KeysetEnumTestDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Gets the entity set.</summary>
    public DbSet<KeysetEnumEntity> Entities => Set<KeysetEnumEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<KeysetEnumEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Day);
            b.Property(e => e.OptionalDay);
            b.Property(e => e.DayNumber);
            b.Ignore(e => e.RowVersion);
        });
}
