using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Administration;

/// <summary>
/// Tests for <see cref="InternalCommandCleanupService"/>: completed rows leave on the retention
/// window, abandoned rows leave on their own (wider) one, and rows still queued are never swept.
/// </summary>
public sealed class InternalCommandCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InternalCommandTestHarness.InternalCommandTestContext _context;
    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch.AddYears(1));
    private readonly ServiceProvider _services;

    public InternalCommandCleanupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = InternalCommandTestHarness.InternalCommandTestContext.Create(_connection);

        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(_context);

        var services = new ServiceCollection();
        services.AddScoped(_ => dbContextFactory.Object);
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PurgeAsync_DeletesCompletedRowsPastRetentionAndKeepsTheRest()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        Seed("stale-done", processedOn: now.AddDays(-30));
        var freshDone = Seed("fresh-done", processedOn: now.AddDays(-1));
        var queued = Seed("queued");

        await CreateService(retentionDays: 7).PurgeAsync(TestContext.Current.CancellationToken);

        (await RemainingIdsAsync()).Should().BeEquivalentTo([freshDone.Id, queued.Id]);
    }

    [Fact]
    public async Task PurgeAsync_KeepsAbandonedRowsUntilTheirOwnWiderWindow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var abandoned = Seed("abandoned", deadLetteredOn: now.AddDays(-10));

        await CreateService(retentionDays: 7, deadLetterRetentionDays: 30)
            .PurgeAsync(TestContext.Current.CancellationToken);

        (await RemainingIdsAsync()).Should().BeEquivalentTo([abandoned.Id]);
    }

    [Fact]
    public async Task PurgeAsync_DeletesAbandonedRowsPastTheirWindow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        Seed("abandoned", deadLetteredOn: now.AddDays(-40));

        await CreateService(retentionDays: 7, deadLetterRetentionDays: 30)
            .PurgeAsync(TestContext.Current.CancellationToken);

        (await RemainingIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeAsync_WithNoDeadLetterWindow_FallsBackToTheCompletedWindow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        Seed("abandoned", deadLetteredOn: now.AddDays(-10));

        await CreateService(retentionDays: 7).PurgeAsync(TestContext.Current.CancellationToken);

        (await RemainingIdsAsync()).Should().BeEmpty();
    }

    private InternalCommandCleanupService CreateService(int retentionDays, int deadLetterRetentionDays = 0)
    {
        var settings = new InternalCommandsSettings
        {
            Enabled = true,
            DataSource = DataSource.Sqlite,
            RetentionDays = retentionDays,
            DeadLetterRetentionDays = deadLetterRetentionDays,
        };

        return new InternalCommandCleanupService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InternalCommandCleanupService>.Instance,
            Options.Create(settings),
            new EmptyEntityDataSourceRegistry(),
            new DefaultDataSourceResolver(),
            _clock);
    }

    private InternalCommandMessage Seed(string value, DateTime? processedOn = null, DateTime? deadLetteredOn = null)
    {
        var row = InternalCommandTestHarness.Seed(
            _context, new RecordingCommand(value), _clock.GetUtcNow().UtcDateTime);

        row.ProcessedOn = processedOn;
        row.DeadLetteredOn = deadLetteredOn;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return row;
    }

    private async Task<List<Guid>> RemainingIdsAsync()
    {
        _context.ChangeTracker.Clear();
        return await _context.Set<InternalCommandMessage>().AsNoTracking()
            .Select(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
