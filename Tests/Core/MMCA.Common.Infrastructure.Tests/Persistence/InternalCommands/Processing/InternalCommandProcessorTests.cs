using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Tests for <see cref="InternalCommandProcessor"/>: which rows a cycle picks up, that execution
/// goes through the decorated handler registration, the claim lease, the retry and dead-letter
/// policy, and the principal restored around a deferred execution.
/// </summary>
public sealed class InternalCommandProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InternalCommandTestHarness.InternalCommandTestContext _context;
    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch);
    private readonly ExecutionLog _log = new();

    /// <summary>
    /// Every provider a test built, disposed together at the end. Held here rather than with a
    /// <c>using</c> per test because the provider is <see cref="IAsyncDisposable"/> and a
    /// <c>using</c> declaration over one is an analyzer error in this repo.
    /// </summary>
    private readonly List<ServiceProvider> _providers = [];

    public InternalCommandProcessorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = InternalCommandTestHarness.InternalCommandTestContext.Create(_connection);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Cycle_ADueCommand_RunsThroughTheDecoratedHandlerAndCompletesTheRow()
    {
        var row = Seed(new RecordingCommand("payload"), InternalCommandTestHarness.EpochUtc);
        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().ContainSingle().Which.Value.Should().Be("payload");
        var stored = await ReloadAsync(row.Id);
        stored.ProcessedOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        stored.Attempts.Should().Be(1);
        stored.ClaimedBy.Should().BeNull("a completed row releases its lease");
        stored.DeadLetteredOn.Should().BeNull();
    }

    [Fact]
    public async Task Cycle_ACommandTheValidatingDecoratorRejects_NeverReachesTheHandlerAndRetries()
    {
        // An empty Value fails RecordingCommandValidator, so a run that never reaches the handler is
        // proof the processor resolved the DECORATED registration rather than the bare handler.
        var row = Seed(new RecordingCommand(string.Empty), InternalCommandTestHarness.EpochUtc);
        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().BeEmpty("the validating decorator short-circuits before the handler");
        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(1);
        stored.ProcessedOn.Should().BeNull();
        stored.DeadLetteredOn.Should().BeNull("one failed attempt of three is a retry, not a dead letter");
        stored.LastError.Should().Contain("Value is required.");
        stored.ClaimedUntil.Should().BeAfter(InternalCommandTestHarness.EpochUtc, "the row is parked for its backoff");
    }

    [Fact]
    public async Task Cycle_ARowScheduledInTheFuture_IsLeftAloneAndDrivesTheSmartWait()
    {
        var runAt = InternalCommandTestHarness.EpochUtc.AddMinutes(10);
        var row = Seed(new RecordingCommand("later"), runAt);
        var processor = CreateProcessor();

        var cycle = await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().BeEmpty();
        cycle.HasMoreDueWork.Should().BeFalse();
        cycle.EarliestUpcoming.Should().Be(runAt);
        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(0);
        stored.ClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task Cycle_ARowUnderAnotherReplicasUnexpiredLease_IsNotRun()
    {
        Seed(
            new RecordingCommand("claimed"),
            InternalCommandTestHarness.EpochUtc,
            claimedUntil: InternalCommandTestHarness.EpochUtc.AddMinutes(5),
            claimedBy: Guid.NewGuid());

        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().BeEmpty("the lease is what stops two replicas running one command");
    }

    [Fact]
    public async Task Cycle_ARowWhoseLeaseHasExpired_IsReclaimedAndRun()
    {
        Seed(
            new RecordingCommand("abandoned"),
            InternalCommandTestHarness.EpochUtc,
            claimedUntil: InternalCommandTestHarness.EpochUtc.AddMinutes(-1),
            claimedBy: Guid.NewGuid());

        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().ContainSingle("a dead replica's rows come back when its lease expires");
    }

    [Fact]
    public async Task Cycle_AHandlerReturningFailureOnItsLastAttempt_DeadLettersTheRow()
    {
        _log.Outcome = () => Result.Failure(Error.Failure("Test.Failed", "the work did not happen"));
        var row = Seed(new RecordingCommand("doomed"), InternalCommandTestHarness.EpochUtc, attempts: 2);
        var processor = CreateProcessor(maxAttempts: 3);

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(3);
        stored.DeadLetteredOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        stored.ProcessedOn.Should().BeNull();
        stored.LastError.Should().Contain("the work did not happen");
        stored.ClaimedBy.Should().BeNull("an abandoned row must not stay leased");
    }

    [Fact]
    public async Task Cycle_AHandlerThatThrowsOnItsLastAttempt_DeadLettersTheRowWithTheMessage()
    {
        _log.Throws = () => new InvalidOperationException("downstream exploded");
        var row = Seed(new RecordingCommand("doomed"), InternalCommandTestHarness.EpochUtc, attempts: 2);
        var processor = CreateProcessor(maxAttempts: 3);

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var stored = await ReloadAsync(row.Id);
        stored.DeadLetteredOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        stored.LastError.Should().Be("downstream exploded");
    }

    [Fact]
    public async Task Cycle_ADeadLetteredRow_IsNeverClaimedAgain()
    {
        var row = Seed(new RecordingCommand("abandoned"), InternalCommandTestHarness.EpochUtc);
        var deadLetteredOn = InternalCommandTestHarness.EpochUtc.AddMinutes(-1);
        await _context.Set<InternalCommandMessage>()
            .Where(c => c.Id == row.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.DeadLetteredOn, (DateTime?)deadLetteredOn),
                TestContext.Current.CancellationToken);
        _context.ChangeTracker.Clear();

        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        _log.Executions.Should().BeEmpty();
    }

    [Fact]
    public async Task Cycle_ARowWhoseCommandTypeCannotBeResolved_IsDeadLetteredImmediately()
    {
        var row = Seed(new RecordingCommand("orphan"), InternalCommandTestHarness.EpochUtc);
        await _context.Set<InternalCommandMessage>()
            .Where(c => c.Id == row.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.CommandType, "Retired.Command, Retired.Assembly"),
                TestContext.Current.CancellationToken);
        _context.ChangeTracker.Clear();

        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var stored = await ReloadAsync(row.Id);
        stored.DeadLetteredOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        stored.LastError.Should().Contain("Cannot resolve command type");
    }

    [Fact]
    public async Task Cycle_ACommandWithNoRegisteredHandler_IsDeadLetteredImmediately()
    {
        var row = Seed(new RecordingCommand("unhandled"), InternalCommandTestHarness.EpochUtc);

        // No handler registration at all: the deployment fact the processor must report rather than
        // retry until the attempt budget runs out.
        var processor = CreateProcessor(registerHandler: false);

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var stored = await ReloadAsync(row.Id);
        stored.DeadLetteredOn.Should().Be(InternalCommandTestHarness.EpochUtc);
        stored.LastError.Should().Contain("No command handler is registered");
    }

    [Fact]
    public async Task Cycle_ARowCarryingAPrincipal_RestoresItForTheExecution()
    {
        var scheduler = InternalCommandTestHarness.CreateScheduler(
            _context,
            InternalCommandTestHarness.Settings(),
            _clock,
            Mock.Of<IInternalCommandSignal>(),
            new StubCurrentUserService(7, ["Admin", "Organizer"]));

        await scheduler.ScheduleAsync(new RecordingCommand("as-user"), runAt: null, TestContext.Current.CancellationToken);
        _context.ChangeTracker.Clear();

        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var execution = _log.Executions.Should().ContainSingle().Subject;
        execution.UserId.Should().Be(7);
        execution.Roles.Should().Equal("Admin", "Organizer");
    }

    [Fact]
    public async Task Cycle_ARowScheduledWithNoUser_ExecutesAnonymously()
    {
        Seed(new RecordingCommand("system"), InternalCommandTestHarness.EpochUtc);
        var processor = CreateProcessor();

        await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        var execution = _log.Executions.Should().ContainSingle().Subject;
        execution.UserId.Should().BeNull("a system-scheduled command must not invent an identity");
        execution.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task Cycle_AFullBatchThatMadeProgress_ReportsMoreDueWork()
    {
        Seed(new RecordingCommand("one"), InternalCommandTestHarness.EpochUtc);
        Seed(new RecordingCommand("two"), InternalCommandTestHarness.EpochUtc);
        var processor = CreateProcessor(batchSize: 2);

        var cycle = await processor.ProcessDueCommandsAsync(TestContext.Current.CancellationToken);

        cycle.HasMoreDueWork.Should().BeTrue();
        _log.Executions.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    public void ComputeRetryBackoffSeconds_DoublesPerAttemptWithinTheJitterBand(int attempts, double unjittered)
    {
        var processor = CreateProcessor();

        var backoff = processor.ComputeRetryBackoffSeconds(attempts);

        backoff.Should().BeInRange(unjittered * 0.8, unjittered * 1.2);
    }

    [Fact]
    public void ComputeRetryBackoffSeconds_IsCappedAtTheConfiguredCeiling()
    {
        var processor = CreateProcessor(maxAttempts: 20, maxRetryBackoffSeconds: 60);

        processor.ComputeRetryBackoffSeconds(19).Should().Be(60);
    }

    [Fact]
    public void ComputeWaitTime_NothingUpcoming_WaitsThePollingInterval()
    {
        var wait = InternalCommandProcessor.ComputeWaitTime(
            null, InternalCommandTestHarness.EpochUtc, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        wait.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ComputeWaitTime_ARowDueSooner_WaitsOnlyUntilThen()
    {
        var wait = InternalCommandProcessor.ComputeWaitTime(
            InternalCommandTestHarness.EpochUtc.AddSeconds(5),
            InternalCommandTestHarness.EpochUtc,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));

        wait.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ComputeWaitTime_AnOverdueRow_IsFlooredAtOneSecond()
    {
        var wait = InternalCommandProcessor.ComputeWaitTime(
            InternalCommandTestHarness.EpochUtc.AddSeconds(-60),
            InternalCommandTestHarness.EpochUtc,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));

        wait.Should().Be(TimeSpan.FromSeconds(1));
    }

    private InternalCommandMessage Seed(
        RecordingCommand command,
        DateTime scheduledOn,
        int attempts = 0,
        DateTime? claimedUntil = null,
        Guid? claimedBy = null)
    {
        var row = InternalCommandTestHarness.Seed(_context, command, scheduledOn, attempts, claimedUntil, claimedBy);
        _context.ChangeTracker.Clear();
        return row;
    }

    private async Task<InternalCommandMessage> ReloadAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Set<InternalCommandMessage>().AsNoTracking()
            .SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
    }

    private InternalCommandProcessor CreateProcessor(
        int maxAttempts = 3,
        int batchSize = 50,
        int maxRetryBackoffSeconds = 600,
        bool registerHandler = true)
    {
        (InternalCommandProcessor processor, ServiceProvider services) = InternalCommandTestHarness.CreateProcessor(
            _context,
            InternalCommandTestHarness.Settings(
                maxAttempts: maxAttempts, batchSize: batchSize, maxRetryBackoffSeconds: maxRetryBackoffSeconds),
            _clock,
            registerHandler
                ? services => InternalCommandTestHarness.AddRecordingCommandPipeline(services, _log)
                : null);

        _providers.Add(services);
        return processor;
    }
}
