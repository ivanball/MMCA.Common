using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.Scheduling;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// Coverage for <see cref="AuditTrailCleanupJob"/>: the retention window it honors, the sources it
/// sweeps, and the two ways it does nothing (the trail is off, or the source has no trail table).
/// </summary>
public sealed class AuditTrailCleanupJobTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = AuditTrailTestHarness.CreateTimeProvider();
    private readonly AuditTrailTestContext _context;

    public AuditTrailCleanupJobTests()
    {
        _context = AuditTrailTestContext.Create(_timeProvider);

        // The sweep runs "now minus RetentionDays", so put the clock well past the seeded rows.
        _timeProvider.SetUtcNow(AuditTrailTestHarness.Epoch.AddDays(200));
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Job_Identity_IsStableAndRunsNightly()
    {
        var job = CreateJob(new AuditTrailSettings { Enabled = true });

        job.Name.Should().Be("audit-trail-cleanup");
        job.CronExpression.Should().Be("0 3 * * *");
    }

    [Fact]
    public async Task ExecuteAsync_DeletesOnlyRowsOlderThanRetention()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await SeedRowsAsync(
            ("expired-a", now.AddDays(-120)),
            ("expired-b", now.AddDays(-91)),
            ("kept-boundary", now.AddDays(-89)),
            ("kept-recent", now.AddDays(-1)));

        var job = CreateJob(new AuditTrailSettings { Enabled = true, RetentionDays = 90, DataSource = DataSource.Sqlite });
        await job.ExecuteAsync(CancellationToken.None);

        var remaining = await _context.TrailRows.AsNoTracking().Select(r => r.EntityKey).ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain("kept-boundary");
        remaining.Should().Contain("kept-recent");
    }

    [Fact]
    public async Task ExecuteAsync_TrailDisabled_DeletesNothing()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await SeedRowsAsync(("ancient", now.AddDays(-3000)));

        var job = CreateJob(new AuditTrailSettings { Enabled = false, RetentionDays = 1 });
        await job.ExecuteAsync(CancellationToken.None);

        (await _context.TrailRows.AsNoTracking().ToListAsync()).Should().ContainSingle(
            "a host that switched the trail off is not asking for its history to be erased");
    }

    [Fact]
    public async Task ExecuteAsync_SourceWithoutTheTrailTable_IsSkippedWithoutThrowing()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var trailless = SchedulerTestHarness.SchedulerTestContext.Create(connection);

        var job = CreateJob(new AuditTrailSettings { Enabled = true, RetentionDays = 1 }, trailless);

        var act = async () => await job.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("the model is the authority on whether a source has the table");
    }

    [Fact]
    public async Task ExecuteAsync_CosmosSources_AreNeverSwept()
    {
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse())
            .Returns([DataSourceKey.Default(DataSource.CosmosDB)]);

        var dbContextFactory = new Mock<IDbContextFactory>();
        var job = new AuditTrailCleanupJob(
            dbContextFactory.Object,
            registry.Object,
            NullLogger<AuditTrailCleanupJob>.Instance,
            Options.Create(new AuditTrailSettings { Enabled = true }),
            _timeProvider);

        await job.ExecuteAsync(CancellationToken.None);

        dbContextFactory.Verify(f => f.GetDbContext(It.IsAny<DataSourceKey>()), Times.Never,
            "Cosmos has no relational trail table to sweep");
    }

    private AuditTrailCleanupJob CreateJob(AuditTrailSettings settings, ApplicationDbContext? context = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns(context ?? _context);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse())
            .Returns([DataSourceKey.Default(DataSource.Sqlite)]);

        return new AuditTrailCleanupJob(
            dbContextFactory.Object,
            registry.Object,
            NullLogger<AuditTrailCleanupJob>.Instance,
            Options.Create(settings),
            _timeProvider);
    }

    private async Task SeedRowsAsync(params (string Key, DateTime ChangedOn)[] rows)
    {
        foreach ((var key, var changedOn) in rows)
        {
            _context.TrailRows.Add(new AuditTrailEntry
            {
                EntityType = typeof(AuditedThing).FullName!,
                EntityKey = key,
                Operation = "Modified",
                ChangedOn = changedOn,
            });
        }

        await _context.SaveChangesAsync();
    }
}
