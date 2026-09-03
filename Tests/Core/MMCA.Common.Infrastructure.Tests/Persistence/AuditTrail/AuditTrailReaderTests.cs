using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Tests.Scheduling;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// Coverage for <see cref="AuditTrailReader"/>: the entity filter, the newest-first order, paging,
/// and the empty results a host gets before it enables the trail.
/// </summary>
public sealed class AuditTrailReaderTests : IDisposable
{
    private const string EntityType = "MMCA.Tests.Order";

    private readonly FakeTimeProvider _timeProvider = AuditTrailTestHarness.CreateTimeProvider();
    private readonly AuditTrailTestContext _context;

    public AuditTrailReaderTests() => _context = AuditTrailTestContext.Create(_timeProvider);

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GetForEntityAsync_ReturnsOnlyThatEntitysHistoryNewestFirst()
    {
        await SeedAsync(
            (EntityType, "1", "Name", 1),
            (EntityType, "1", "Quantity", 3),
            (EntityType, "2", "Name", 2),
            ("MMCA.Tests.Other", "1", "Name", 4));

        var rows = await CreateReader().GetForEntityAsync(EntityType, "1");

        rows.Select(r => r.PropertyName).Should().Equal("Quantity", "Name");
        rows.Should().OnlyContain(r => r.EntityType == EntityType && r.EntityKey == "1");
    }

    [Fact]
    public async Task GetForEntityAsync_PagesThroughTheHistory()
    {
        await SeedAsync(
            (EntityType, "1", "First", 1),
            (EntityType, "1", "Second", 2),
            (EntityType, "1", "Third", 3));

        var reader = CreateReader();

        (await reader.GetForEntityAsync(EntityType, "1", page: 1, pageSize: 2))
            .Select(r => r.PropertyName).Should().Equal("Third", "Second");
        (await reader.GetForEntityAsync(EntityType, "1", page: 2, pageSize: 2))
            .Select(r => r.PropertyName).Should().Equal("First");
        (await reader.GetForEntityAsync(EntityType, "1", page: 3, pageSize: 2))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task GetForEntityAsync_NonPositivePagingArguments_AreClampedRatherThanThrowing()
    {
        await SeedAsync((EntityType, "1", "First", 1), (EntityType, "1", "Second", 2));

        var rows = await CreateReader().GetForEntityAsync(EntityType, "1", page: 0, pageSize: 0);

        rows.Should().ContainSingle().Which.PropertyName.Should().Be("Second");
    }

    [Fact]
    public async Task GetForEntityAsync_ProjectsEveryRecordedField()
    {
        await SeedAsync((EntityType, "1", "Name", 1));

        var row = (await CreateReader().GetForEntityAsync(EntityType, "1")).Single();

        row.Operation.Should().Be("Modified");
        row.OldValue.Should().Be("old-Name");
        row.NewValue.Should().Be("new-Name");
        row.ChangedBy.Should().Be(9);
        row.CorrelationId.Should().Be("trace-Name");
    }

    [Fact]
    public async Task GetForEntityAsync_EntityWithNoHistory_ReturnsEmpty()
    {
        await SeedAsync((EntityType, "1", "Name", 1));

        (await CreateReader().GetForEntityAsync(EntityType, "does-not-exist")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetForEntityAsync_WhenTheSourceHasNoTrailTable_ReturnsEmpty()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var trailless = SchedulerTestHarness.SchedulerTestContext.Create(connection);

        var rows = await CreateReader(trailless).GetForEntityAsync(EntityType, "1");

        rows.Should().BeEmpty("the read surface is registered before the trail is switched on");
    }

    private AuditTrailReader CreateReader(ApplicationDbContext? context = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns(context ?? _context);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        return new AuditTrailReader(
            dbContextFactory.Object,
            resolver.Object,
            Options.Create(new AuditTrailSettings { Enabled = true, DataSource = DataSource.Sqlite }));
    }

    private async Task SeedAsync(params (string EntityType, string EntityKey, string PropertyName, int MinutesOffset)[] rows)
    {
        foreach (var row in rows)
        {
            _context.TrailRows.Add(new AuditTrailEntry
            {
                EntityType = row.EntityType,
                EntityKey = row.EntityKey,
                PropertyName = row.PropertyName,
                OldValue = "old-" + row.PropertyName,
                NewValue = "new-" + row.PropertyName,
                Operation = "Modified",
                ChangedBy = 9,
                ChangedOn = AuditTrailTestHarness.EpochUtc.AddMinutes(row.MinutesOffset),
                CorrelationId = "trace-" + row.PropertyName,
            });
        }

        await _context.SaveChangesAsync();
    }
}
