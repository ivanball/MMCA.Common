using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Retention;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Retention;

/// <summary>
/// Coverage for the batched retention delete every purge shares: it removes the matching rows across
/// batches, reports the total, stops between batches when cancelled, keeps the count when a batch
/// fails, and refuses a soft-deletable entity.
/// </summary>
public sealed class BatchedDeleteTests : IAsyncLifetime
{
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DeleteCounter _deletes = new();
    private readonly List<int> _reported = [];
    private RowContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<RowContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_deletes)
            .Options;

        _context = new RowContext(options);
        await _context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheMatchingRowsInBatches_AndReportsTheTotalOnce()
    {
        await SeedAsync(expired: 25, live: 5);

        await DeleteExpiredAsync(TestContext.Current.CancellationToken);

        _reported.Should().Equal(25);
        (await _context.Rows.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(5);
        (await _context.Rows.AsNoTracking().AllAsync(r => r.ChangedOn >= Cutoff, TestContext.Current.CancellationToken)).Should().BeTrue();
        _deletes.Count.Should().Be(3, "25 rows at 10 per statement take three deletes");
    }

    [Fact]
    public async Task DeleteAsync_WithNothingToRemove_ReportsZeroAndIssuesNoDelete()
    {
        await SeedAsync(expired: 0, live: 3);

        await DeleteExpiredAsync(TestContext.Current.CancellationToken);

        _reported.Should().Equal(0);
        _deletes.Count.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_CancelledBetweenBatches_StopsAndReportsWhatWasDeleted()
    {
        await SeedAsync(expired: 25, live: 0);
        using var cts = new CancellationTokenSource();
        _deletes.OnDeleted = () => cts.Cancel();

        await DeleteExpiredAsync(cts.Token);

        _reported.Should().Equal(10);
        (await _context.Rows.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(15);
        _deletes.Count.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_WhenALaterBatchThrows_StillReportsTheEarlierDeletes()
    {
        await SeedAsync(expired: 25, live: 0);
        _deletes.FailOnDeleteNumber = 3;

        var act = () => DeleteExpiredAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database lost");
        _reported.Should().Equal(20);
        (await _context.Rows.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(5);
    }

    [Fact]
    public async Task DeleteAsync_ForASoftDeletableEntity_ThrowsAndIssuesNoDelete()
    {
        var act = () => BatchedDelete.DeleteAsync<SoftDeletableRow, DateTime>(
            _context,
            r => r.CreatedOn < Cutoff,
            r => r.CreatedOn,
            _reported.Add,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*soft-deletable*");
        _deletes.Count.Should().Be(0);
    }

    private Task DeleteExpiredAsync(CancellationToken cancellationToken) =>
        BatchedDelete.DeleteAsync<Row, DateTime>(
            _context,
            r => r.ChangedOn < Cutoff,
            r => r.ChangedOn,
            _reported.Add,
            cancellationToken,
            batchSize: 10);

    private async Task SeedAsync(int expired, int live)
    {
        for (var index = 0; index < expired; index++)
        {
            _context.Rows.Add(new Row { Id = Guid.NewGuid(), ChangedOn = Cutoff.AddDays(-1 - index) });
        }

        for (var index = 0; index < live; index++)
        {
            _context.Rows.Add(new Row { Id = Guid.NewGuid(), ChangedOn = Cutoff.AddDays(1 + index) });
        }

        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        _context.ChangeTracker.Clear();
    }

    private sealed class Row
    {
        public required Guid Id { get; init; }

        public required DateTime ChangedOn { get; init; }
    }

    private sealed class SoftDeletableRow : IAuditableEntity
    {
        public bool IsDeleted => false;

        public DateTime CreatedOn => default;

        public UserIdentifierType CreatedBy => default;

        public DateTime? LastModifiedOn => null;

        public UserIdentifierType? LastModifiedBy => null;

        public DateTime? DeletedOn => null;

        public UserIdentifierType? DeletedBy => null;
    }

    private sealed class RowContext(DbContextOptions<RowContext> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Row>(entity => entity.HasKey(e => e.Id));
    }

    /// <summary>Counts the DELETE statements a sweep issues; can cancel after the first or fail a later one.</summary>
    private sealed class DeleteCounter : DbCommandInterceptor
    {
        private int _attempts;

        public int Count { get; private set; }

        public Action? OnDeleted { get; set; }

        public int? FailOnDeleteNumber { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsDelete(command) && ++_attempts == FailOnDeleteNumber)
            {
                throw new InvalidOperationException("database lost");
            }

            return new ValueTask<InterceptionResult<int>>(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsDelete(command))
            {
                Count++;
                OnDeleted?.Invoke();
            }

            return new ValueTask<int>(result);
        }

        private static bool IsDelete(DbCommand command) =>
            command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase);
    }
}
