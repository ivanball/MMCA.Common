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
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Administration;

/// <summary>
/// Tests for <see cref="InternalCommandAdministration"/>: the operator surface an internal command
/// needs to be counted, inspected, put back in the queue, or purged.
/// </summary>
public sealed class InternalCommandAdministrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InternalCommandTestHarness.InternalCommandTestContext _context;
    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch);
    private readonly Mock<IInternalCommandSignal> _signal = new();
    private readonly ServiceProvider _services;
    private readonly InternalCommandAdministration _sut;

    public InternalCommandAdministrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = InternalCommandTestHarness.InternalCommandTestContext.Create(_connection);

        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(_context);

        var services = new ServiceCollection();
        services.AddScoped(_ => dbContextFactory.Object);
        _services = services.BuildServiceProvider();

        _sut = new InternalCommandAdministration(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InternalCommandAdministration>.Instance,
            Options.Create(InternalCommandTestHarness.Settings()),
            new EmptyEntityDataSourceRegistry(),
            new DefaultDataSourceResolver(),
            _signal.Object,
            _clock);
    }

    public void Dispose()
    {
        _services.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CountPendingAsync_CountsOnlyRowsStillAwaitingExecution()
    {
        Seed("queued");
        Seed("upcoming", scheduledOn: InternalCommandTestHarness.EpochUtc.AddHours(1));
        Seed("done", processedOn: InternalCommandTestHarness.EpochUtc);
        Seed("abandoned", deadLetteredOn: InternalCommandTestHarness.EpochUtc);

        var result = await _sut.CountPendingAsync(null, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2, "a future row is accepted backlog; a completed or abandoned one is not");
    }

    [Fact]
    public async Task ListDeadLettersAsync_ReturnsOnlyAbandonedRowsOldestFirst()
    {
        Seed("queued");
        var second = Seed(
            "later-failure",
            scheduledOn: InternalCommandTestHarness.EpochUtc.AddMinutes(5),
            deadLetteredOn: InternalCommandTestHarness.EpochUtc.AddMinutes(6));
        var first = Seed("early-failure", deadLetteredOn: InternalCommandTestHarness.EpochUtc.AddMinutes(1));

        var result = await _sut.ListDeadLettersAsync(null, 0, 10, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var deadLetters = result.Value!;
        deadLetters.Select(d => d.Id).Should().Equal(first.Id, second.Id);
        deadLetters[0].CommandType.Should().Contain(nameof(RecordingCommand));
    }

    [Fact]
    public async Task ListDeadLettersAsync_RejectsAnOutOfRangePage()
    {
        (await _sut.ListDeadLettersAsync(null, -1, 10, TestContext.Current.CancellationToken)).IsFailure.Should().BeTrue();
        (await _sut.ListDeadLettersAsync(null, 0, 0, TestContext.Current.CancellationToken)).IsFailure.Should().BeTrue();
        (await _sut.ListDeadLettersAsync(null, 0, 501, TestContext.Current.CancellationToken)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task RequeueAsync_ClearsTheAbandonmentAndTheClaimButKeepsTheError()
    {
        var row = Seed(
            "abandoned",
            attempts: 3,
            deadLetteredOn: InternalCommandTestHarness.EpochUtc,
            lastError: "downstream exploded",
            claimedBy: Guid.NewGuid(),
            claimedUntil: InternalCommandTestHarness.EpochUtc.AddMinutes(5));

        var result = await _sut.RequeueAsync(null, [row.Id], TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);

        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(0);
        stored.DeadLetteredOn.Should().BeNull();
        stored.ClaimedBy.Should().BeNull();
        stored.ClaimedUntil.Should().BeNull();
        stored.LastError.Should().Be("downstream exploded", "the reason it needed requeuing is the evidence");
        _signal.Verify(s => s.Signal(), Times.Once, "a requeued row should not wait out the polling interval");
    }

    [Fact]
    public async Task RequeueAsync_WithNoIds_RequeuesEveryDeadLetterAndLeavesQueuedRowsAlone()
    {
        var queued = Seed("queued", attempts: 1);
        Seed("abandoned-one", attempts: 3, deadLetteredOn: InternalCommandTestHarness.EpochUtc);
        Seed("abandoned-two", attempts: 3, deadLetteredOn: InternalCommandTestHarness.EpochUtc);

        var result = await _sut.RequeueAsync(null, null, TestContext.Current.CancellationToken);

        result.Value.Should().Be(2);
        (await ReloadAsync(queued.Id)).Attempts.Should().Be(1, "a row that is merely retrying is not a dead letter");
    }

    [Fact]
    public async Task PurgeProcessedAsync_DeletesOnlyCompletedRowsPastTheThreshold()
    {
        var recent = Seed("recent", processedOn: InternalCommandTestHarness.EpochUtc.AddMinutes(-1));
        Seed("old", processedOn: InternalCommandTestHarness.EpochUtc.AddDays(-30));
        var abandoned = Seed("abandoned", deadLetteredOn: InternalCommandTestHarness.EpochUtc.AddDays(-30));

        var result = await _sut.PurgeProcessedAsync(null, TimeSpan.FromDays(7), TestContext.Current.CancellationToken);

        result.Value.Should().Be(1);
        _context.ChangeTracker.Clear();
        var remaining = await _context.Set<InternalCommandMessage>().AsNoTracking()
            .Select(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remaining.Should().BeEquivalentTo([recent.Id, abandoned.Id]);
    }

    [Fact]
    public async Task PurgeProcessedAsync_RejectsANegativeThreshold()
    {
        var result = await _sut.PurgeProcessedAsync(null, TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AnyOperation_OnAnUnknownDataSource_FailsRatherThanThrowing()
    {
        var result = await _sut.CountPendingAsync("Sqlite/Nowhere", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("InternalCommands.UnknownDataSource");
    }

    private InternalCommandMessage Seed(
        string value,
        DateTime? scheduledOn = null,
        int attempts = 0,
        DateTime? processedOn = null,
        DateTime? deadLetteredOn = null,
        string? lastError = null,
        Guid? claimedBy = null,
        DateTime? claimedUntil = null)
    {
        var row = InternalCommandTestHarness.Seed(
            _context,
            new RecordingCommand(value),
            scheduledOn ?? InternalCommandTestHarness.EpochUtc,
            attempts,
            claimedUntil,
            claimedBy);

        row.ProcessedOn = processedOn;
        row.DeadLetteredOn = deadLetteredOn;
        row.LastError = lastError;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return row;
    }

    private async Task<InternalCommandMessage> ReloadAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Set<InternalCommandMessage>().AsNoTracking()
            .SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
    }
}
