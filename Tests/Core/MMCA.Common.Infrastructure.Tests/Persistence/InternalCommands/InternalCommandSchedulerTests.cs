using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// Tests for <see cref="InternalCommandScheduler"/>: the atomicity contract (a row enrolled in the
/// caller's transaction lives or dies with it), the immediate-save path outside a transaction, and
/// the scheduled instant the two overloads compute.
/// </summary>
public sealed class InternalCommandSchedulerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InternalCommandTestHarness.InternalCommandTestContext _context;
    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch);
    private readonly Mock<IInternalCommandSignal> _signal = new();

    public InternalCommandSchedulerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = InternalCommandTestHarness.InternalCommandTestContext.Create(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ScheduleAsync_InsideATransactionThatRollsBack_PersistsNothing()
    {
        var sut = CreateScheduler();

        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            var scheduled = await sut.ScheduleAsync(new RecordingCommand("work"), runAt: null, TestContext.Current.CancellationToken);
            scheduled.IsSuccess.Should().BeTrue();

            // The Transactional decorator's shape: the handler's own save writes the row, and the
            // rollback that follows a business failure takes it with it.
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        _context.ChangeTracker.Clear();
        var rows = await _context.Set<InternalCommandMessage>().ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().BeEmpty("a rolled-back transaction must schedule nothing");
    }

    [Fact]
    public async Task ScheduleAsync_InsideATransactionThatCommits_PersistsTheRow()
    {
        var sut = CreateScheduler();

        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            await sut.ScheduleAsync(new RecordingCommand("work"), runAt: null, TestContext.Current.CancellationToken);
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.ScheduledOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        row.CreatedOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        row.ProcessedOn.Should().BeNull();
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_InsideATransaction_DoesNotSignalTheProcessor()
    {
        var sut = CreateScheduler();

        await using var transaction = await _context.Database.BeginTransactionAsync();
        await sut.ScheduleAsync(new RecordingCommand("work"), runAt: null, TestContext.Current.CancellationToken);

        _signal.Verify(s => s.Signal(), Times.Never,
            "a signal before the commit would only buy a poll against an uncommitted transaction");
    }

    [Fact]
    public async Task ScheduleAsync_OutsideATransaction_SavesImmediatelyAndSignals()
    {
        var sut = CreateScheduler();

        var scheduled = await sut.ScheduleAsync(new RecordingCommand("work"), runAt: null, TestContext.Current.CancellationToken);

        scheduled.IsSuccess.Should().BeTrue();
        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(scheduled.Value);
        _signal.Verify(s => s.Signal(), Times.Once);
    }

    [Fact]
    public async Task ScheduleAsync_WithAFutureInstant_StoresItAndDoesNotSignal()
    {
        var sut = CreateScheduler();
        var runAt = InternalCommandTestHarness.Epoch.AddMinutes(30);

        await sut.ScheduleAsync(new RecordingCommand("later"), runAt, TestContext.Current.CancellationToken);

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.ScheduledOn.Should().Be(runAt.UtcDateTime);
        _signal.Verify(s => s.Signal(), Times.Never, "nothing is due yet, so there is nothing to wake for");
    }

    [Fact]
    public async Task ScheduleAsync_WithAPastInstant_NormalizesToNow()
    {
        var sut = CreateScheduler();

        await sut.ScheduleAsync(
            new RecordingCommand("overdue"),
            InternalCommandTestHarness.Epoch.AddHours(-3),
            TestContext.Current.CancellationToken);

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.ScheduledOn.Should().Be(InternalCommandTestHarness.EpochUtc);
    }

    [Fact]
    public async Task ScheduleAsync_WithADelay_AddsItToTheCurrentInstant()
    {
        var sut = CreateScheduler();

        await sut.ScheduleAsync(new RecordingCommand("later"), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.ScheduledOn.Should().Be(InternalCommandTestHarness.EpochUtc.AddMinutes(5));
    }

    [Fact]
    public async Task ScheduleAsync_WithANonPositiveDelay_SchedulesImmediately()
    {
        var sut = CreateScheduler();

        await sut.ScheduleAsync(new RecordingCommand("now"), TimeSpan.Zero, TestContext.Current.CancellationToken);

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.ScheduledOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        _signal.Verify(s => s.Signal(), Times.Once);
    }

    [Fact]
    public async Task ScheduleAsync_CapturesTheSchedulingPrincipal()
    {
        var sut = CreateScheduler(new StubCurrentUserService(42, ["Admin", "Organizer"]));

        await sut.ScheduleAsync(new RecordingCommand("work"), runAt: null, TestContext.Current.CancellationToken);

        _context.ChangeTracker.Clear();
        var row = await _context.Set<InternalCommandMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.UserId.Should().Be(42);
        row.UserRoles.Should().Be("Admin,Organizer");
    }

    private InternalCommandScheduler CreateScheduler(
        MMCA.Common.Application.Interfaces.Infrastructure.Auth.ICurrentUserService? currentUser = null) =>
        InternalCommandTestHarness.CreateScheduler(
            _context,
            InternalCommandTestHarness.Settings(),
            _clock,
            _signal.Object,
            currentUser);
}
