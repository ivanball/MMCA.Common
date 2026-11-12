using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// Scaffolding for the Phase 2 delivery-lane tests (design-webhook-lane.md 2(a): "the test harness gains a
/// context per scope over a temp-file SQLite database"). Concurrent rows cannot share the Phase 1
/// harness's single context (<see cref="InternalCommandLaneTestHarness"/>, which every scope resolves to
/// the same instance), so here every DI scope gets its OWN context over one temp-file database, and
/// every statement any context runs is recorded with the number of the context that ran it.
/// <para>
/// Kept in its own file so the Phase 1 harness and the shared harness the equivalence suite runs on stay
/// as they are. The probe command and its handler are distinct from the Phase 1 probe, so the per-command
/// and per-lane metric assertions of the Phase 1 lane tests never see this class's rows, even when the
/// two test classes run in parallel.
/// </para>
/// </summary>
internal sealed class InternalCommandLaneDatabase : IDisposable
{
    /// <summary>The lane name these tests use unless one asks for its own.</summary>
    public const string DefaultLaneName = "probe-concurrent";

    /// <summary>The text a charged attempt stores in <c>LastError</c> (design L1.10, open question 8).</summary>
    public const string CoolingDownError = InternalCommandLaneTestHarness.CoolingDownError;

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ServiceProvider _contextServices;
    private readonly List<IDisposable> _owned = [];
    private int _contextCount;

    /// <summary>Creates the temp-file database and its table.</summary>
    /// <param name="clock">The fake clock every lane, row runner and probe of this database reads.</param>
    public InternalCommandLaneDatabase(FakeTimeProvider clock)
    {
        Clock = clock;
        Probes = new ConcurrentProbeLog(clock);
        _path = Path.Combine(Path.GetTempPath(), $"mmca-lane-{Guid.NewGuid():N}.db");

        // No pooling: every context opens and closes its own connection, and the file can be deleted at
        // the end. Default Timeout makes a writer that meets another writer's lock wait instead of fail.
        _connectionString = $"Data Source={_path};Pooling=False;Default Timeout=30";
        _contextServices = BuildContextServices();

        using var context = CreateContext(number: 0);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>Gets the stored name of <see cref="ConcurrentProbeCommand"/>, the command type the test lanes claim.</summary>
    public static string ProbeCommandType { get; } = InternalCommandNameResolver.GetStorageName(typeof(ConcurrentProbeCommand));

    /// <summary>Gets the fake clock.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Gets the probe log the handler of every lane of this database writes to.</summary>
    public ConcurrentProbeLog Probes { get; }

    /// <summary>Gets every statement every context ran, and the one-shot fault switch.</summary>
    public LaneDatabaseCommandLog Commands { get; } = new();

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Builds a test lane over <see cref="ConcurrentProbeCommand"/>, keyed by its receiver, with the two
    /// Phase 2 members (design 2(a)): <c>MaxConcurrency</c> and <c>BatchStartBudget</c>. The first four
    /// arguments stay positional as Phase 1 declared them; the two new members are passed by name so the
    /// implementer may order them as they like.
    /// </summary>
    /// <param name="maxConcurrency">How many rows of one batch may run at once.</param>
    /// <param name="batchStartBudget">How long after a batch's first start new rows may start (the webhook lane uses <c>TimeoutSeconds</c>).</param>
    /// <param name="cooldown">The configured receiver cooldown; 60 seconds when omitted.</param>
    /// <param name="name">The lane name (metric tag and log value).</param>
    /// <returns>The lane.</returns>
    public static InternalCommandLane Lane(
        int maxConcurrency,
        TimeSpan batchStartBudget,
        TimeSpan? cooldown = null,
        string name = DefaultLaneName) =>
        new(
            name,
            ProbeCommandType,
            cooldown ?? TimeSpan.FromSeconds(60),
            command => ((ConcurrentProbeCommand)command).Receiver,
            MaxConcurrency: maxConcurrency,
            BatchStartBudget: batchStartBudget);

    /// <summary>
    /// Builds a lane processor and its not-started row runner over this database. Each call gets its own
    /// provider, row runner and cooldown state, so two calls are two replicas sharing one database, one
    /// clock and one probe log. Every scope either creates resolves its own context; only an execution
    /// scope resolves <see cref="ICorrelationContext"/>, which the probe log counts.
    /// </summary>
    public LaneUnderTest CreateLane(
        InternalCommandsSettings settings,
        InternalCommandLane lane,
        ILogger<InternalCommandLaneProcessor>? logger = null,
        ILogger<InternalCommandProcessor>? rowsLogger = null,
        IInternalCommandSignal? signal = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext(Interlocked.Increment(ref _contextCount)));
        services.AddScoped(provider =>
        {
            var factory = new Mock<IDbContextFactory>();
            factory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>()))
                .Returns(() => provider.GetRequiredService<LaneDatabaseContext>());
            return factory.Object;
        });
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext>(_ =>
        {
            Probes.CountExecutionScope();
            return new CorrelationContext();
        });
        services.AddScoped<ScopedUserOverride>();
        services.AddScoped<ICurrentUserService>(provider => new ImpersonatingCurrentUserService(
            new AnonymousCurrentUserService(),
            provider.GetRequiredService<ScopedUserOverride>()));
        services.AddSingleton(Probes);
        services.AddScoped<ICommandHandler<ConcurrentProbeCommand, Result>, ConcurrentProbeHandler>();

        var scopeServices = services.BuildServiceProvider();
        var tableTargets = new FrameworkTableTargets(new EmptyEntityDataSourceRegistry(), new DefaultDataSourceResolver());

        var rows = new InternalCommandProcessor(
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            rowsLogger ?? NullLogger<InternalCommandProcessor>.Instance,
            Options.Create(settings),
            Mock.Of<IInternalCommandSignal>(),
            tableTargets,
            Clock);

        var laneProcessor = new InternalCommandLaneProcessor(
            rows,
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<InternalCommandLaneProcessor>.Instance,
            signal ?? Mock.Of<IInternalCommandSignal>(),
            tableTargets,
            Clock,
            lane);

        var created = new LaneUnderTest(laneProcessor, rows, scopeServices);
        _owned.Add(created);
        return created;
    }

    /// <summary>Seeds one probe row directly, on a context of the test's own (context number 0).</summary>
    public InternalCommandMessage Seed(
        string receiver,
        string value,
        DateTime scheduledOn,
        int attempts = 0,
        DateTime? claimedUntil = null,
        Guid? claimedBy = null)
    {
        using var context = CreateContext(number: 0);
        var row = InternalCommandMessage.FromCommand(new ConcurrentProbeCommand(receiver, value), scheduledOn, scheduledOn, default);
        row.Attempts = attempts;
        row.ClaimedUntil = claimedUntil;
        row.ClaimedBy = claimedBy;
        context.Add(row);
        context.SaveChanges();
        return row;
    }

    /// <summary>Reads one row back as stored.</summary>
    public async Task<InternalCommandMessage> ReloadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(number: 0);
        return await context.Set<InternalCommandMessage>().AsNoTracking()
            .SingleAsync(c => c.Id == id, cancellationToken);
    }

    /// <summary>Plays another replica taking rows over: their claim becomes <paramref name="foreignToken"/> until <paramref name="until"/>.</summary>
    public async Task TakeOverAsync(IReadOnlyCollection<Guid> ids, Guid foreignToken, DateTime until, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(number: 0);
        await context.Set<InternalCommandMessage>()
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.ClaimedBy, foreignToken)
                    .SetProperty(c => c.ClaimedUntil, until),
                cancellationToken);
    }

    /// <summary>
    /// Opens <paramref name="receiver"/>'s cooldown on <paramref name="lane"/> at the current instant with
    /// one real failed attempt of a row on its last attempt, which dead-letters and leaves nothing behind
    /// (the Phase 1 tests' OpenCooldownAsync shape). The receiver succeeds afterwards.
    /// </summary>
    public async Task OpenCooldownAsync(LaneUnderTest lane, string receiver, int maxAttempts, CancellationToken cancellationToken)
    {
        var poison = $"poison-{receiver}";
        var row = Seed(receiver, poison, Now.AddHours(-1), attempts: maxAttempts - 1);
        Probes.Behave(poison, (_, _) => Task.FromResult(ConcurrentProbeLog.FailedDelivery()));

        await lane.Lane.ProcessDueCommandsAsync(cancellationToken);

        (await ReloadAsync(row.Id, cancellationToken)).DeadLetteredOn.Should().NotBeNull(
            "setup: the poison row's failed real attempt is what opens the cooldown");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _contextServices.Dispose();
        try
        {
            File.Delete(_path);
            File.Delete(_path + "-wal");
            File.Delete(_path + "-shm");
        }
        catch (IOException)
        {
            // A temp file left behind is harmless.
        }
    }

    private static ServiceProvider BuildContextServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
            Mock.Of<IDomainEventDispatcher>(),
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            Mock.Of<IOutboxSignal>(), timeProvider: TimeProvider.System));
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        return services.BuildServiceProvider();
    }

    private LaneDatabaseContext CreateContext(int number)
    {
        var options = new DbContextOptionsBuilder<LaneDatabaseContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(Commands)
            .Options;

        return new LaneDatabaseContext(options, _contextServices, number);
    }
}

/// <summary>
/// A test <see cref="ApplicationDbContext"/> over the temp-file database, mapping only
/// <see cref="InternalCommandMessage"/> exactly as <c>InternalCommandTestHarness.InternalCommandTestContext</c>
/// does. <see cref="Number"/> tells the contexts apart in <see cref="LaneDatabaseCommandLog"/>; 0 is the
/// test's own seeding and reading.
/// </summary>
internal sealed class LaneDatabaseContext : ApplicationDbContext
{
    public LaneDatabaseContext(DbContextOptions<LaneDatabaseContext> options, IServiceProvider serviceProvider, int number)
        : base(options, serviceProvider, new NoConfigurationAssemblies(), TestPhysicalDataSources.Sqlite())
    {
        Number = number;
    }

    /// <summary>Gets the creation number of this context; 0 for the test's own contexts.</summary>
    public int Number { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InternalCommandMessage>(entity =>
        {
            entity.ToTable("InternalCommands");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CommandType).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(4000);
            entity.Property(e => e.UserRoles).HasMaxLength(512);
            entity.HasIndex(e => e.ScheduledOn);
        });
    }

    private sealed class NoConfigurationAssemblies : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

/// <summary>One statement a <see cref="LaneDatabaseContext"/> ran.</summary>
/// <param name="Context">The number of the context that ran it (0 = the test's own).</param>
/// <param name="Sql">The command text.</param>
/// <param name="Ids">Every GUID among its parameter values (row ids and claim tokens alike).</param>
internal sealed record LaneDatabaseCommand(int Context, string Sql, IReadOnlySet<Guid> Ids)
{
    /// <summary>Gets a value indicating whether the statement is an UPDATE.</summary>
    public bool IsUpdate => Sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a value indicating whether the statement is the batch fetch (the only ordered SELECT).</summary>
    public bool IsFetch => Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        && Sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Records every statement every context of one <see cref="InternalCommandLaneDatabase"/> runs, and can
/// make the next UPDATE that names one row throw, which is how a test makes an exception escape ONE row
/// outside its handler while other rows keep running (a renamed table would fail them all).
/// </summary>
internal sealed class LaneDatabaseCommandLog : DbCommandInterceptor
{
    /// <summary>The start of the message of the injected fault, so a test can tell it from any other exception.</summary>
    public const string InjectedFailureMessage = "Injected failure of the UPDATE for row";

    private readonly ConcurrentQueue<LaneDatabaseCommand> _commands = new();
    private readonly TaskCompletionSource _injected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Lock _gate = new();
    private Guid? _failNextUpdateOf;

    /// <summary>Gets every recorded statement, in execution order.</summary>
    public IReadOnlyList<LaneDatabaseCommand> All => [.. _commands];

    /// <summary>Gets a task that completes once the armed fault has been thrown.</summary>
    public Task Injected => _injected.Task;

    /// <summary>
    /// Arms a one-shot fault: the next UPDATE whose parameters name <paramref name="rowId"/> throws an
    /// <see cref="InvalidOperationException"/> whose message starts with <see cref="InjectedFailureMessage"/>.
    /// </summary>
    /// <param name="rowId">The row whose next UPDATE fails.</param>
    public void FailNextUpdateOf(Guid rowId)
    {
        lock (_gate)
        {
            _failNextUpdateOf = rowId;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static HashSet<Guid> GuidsIn(DbCommand command)
    {
        var ids = new HashSet<Guid>();
        foreach (DbParameter parameter in command.Parameters)
        {
            switch (parameter.Value)
            {
                case Guid id:
                    ids.Add(id);
                    break;
                case string text when Guid.TryParse(text, out var parsed):
                    ids.Add(parsed);
                    break;
            }
        }

        return ids;
    }

    private void Record(DbCommand command, CommandEventData eventData)
    {
        var number = eventData.Context is LaneDatabaseContext context ? context.Number : -1;
        var recorded = new LaneDatabaseCommand(number, command.CommandText, GuidsIn(command));
        _commands.Enqueue(recorded);

        if (!recorded.IsUpdate)
        {
            return;
        }

        Guid failing;
        lock (_gate)
        {
            if (_failNextUpdateOf is not { } armed || !recorded.Ids.Contains(armed))
            {
                return;
            }

            failing = armed;
            _failNextUpdateOf = null;
        }

        _injected.TrySetResult();
        throw new InvalidOperationException($"{InjectedFailureMessage} {failing}.");
    }
}

/// <summary>A delivery stand-in for the Phase 2 tests: one row per receiver, keyed by <paramref name="Receiver"/>.</summary>
/// <param name="Receiver">The receiver key the test lane reads.</param>
/// <param name="Value">Identifies the row; behaviours are set per value.</param>
internal sealed record ConcurrentProbeCommand(string Receiver, string Value) : IInternalCommand;

/// <summary>One handler invocation.</summary>
/// <param name="Receiver">The receiver of the row.</param>
/// <param name="Value">The row's value.</param>
/// <param name="At">The fake-clock instant the invocation started.</param>
internal sealed record ConcurrentProbeInvocation(string Receiver, string Value, DateTimeOffset At);

/// <summary>
/// Shared sink and per-row behaviour switch for <see cref="ConcurrentProbeHandler"/>. A row with no
/// behaviour succeeds at once. A held row waits until the test releases it with an outcome. The log
/// tracks how many invocations run at once, overall and per receiver, and lets a test await a state
/// (for example "three rows have started") instead of sleeping.
/// </summary>
/// <param name="clock">The fake clock the invocation instants are read from.</param>
internal sealed class ConcurrentProbeLog(TimeProvider clock)
{
    private readonly System.Threading.Lock _gate = new();
    private readonly ConcurrentDictionary<string, Func<ConcurrentProbeCommand, CancellationToken, Task<Result>>> _behaviours =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Result>> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _inFlightPerReceiver = new(StringComparer.Ordinal);
    private readonly List<ConcurrentProbeInvocation> _invocations = [];
    private readonly List<string> _finished = [];
    private readonly List<(Func<bool> Condition, TaskCompletionSource Reached)> _waiters = [];
    private int _inFlight;
    private int _maxInFlight;
    private int _maxInFlightPerReceiver;
    private int _executionScopes;

    /// <summary>Gets every invocation, in the order the handler was entered.</summary>
    public IReadOnlyList<ConcurrentProbeInvocation> Invocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _invocations];
            }
        }
    }

    /// <summary>Gets the values of the invocations, in the order the handler was entered.</summary>
    public IReadOnlyList<string> StartedValues => [.. Invocations.Select(i => i.Value)];

    /// <summary>Gets the values whose handler has returned or thrown, in that order.</summary>
    public IReadOnlyList<string> FinishedValues
    {
        get
        {
            lock (_gate)
            {
                return [.. _finished];
            }
        }
    }

    /// <summary>Gets how many invocations are running now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Gets the most invocations that ran at the same time.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Gets the most invocations of ONE receiver that ran at the same time.</summary>
    public int MaxInFlightPerReceiver => Volatile.Read(ref _maxInFlightPerReceiver);

    /// <summary>Gets how many execution scopes have been created.</summary>
    public int ExecutionScopes => Volatile.Read(ref _executionScopes);

    /// <summary>The failure a failing receiver returns: what the delivery handler returns for a 503.</summary>
    /// <returns>A failed result.</returns>
    public static Result FailedDelivery() =>
        Result.Failure(Error.Failure("Probe.DeliveryFailed", "The receiver answered 503."));

    /// <summary>The values of the rows sent to <paramref name="receiver"/>, in order.</summary>
    /// <param name="receiver">The receiver.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<string> ValuesFor(string receiver) =>
        [.. Invocations.Where(i => string.Equals(i.Receiver, receiver, StringComparison.Ordinal)).Select(i => i.Value)];

    /// <summary>Sets what the handler does for the row whose value is <paramref name="value"/>.</summary>
    /// <param name="value">The row's value.</param>
    /// <param name="behaviour">The behaviour.</param>
    public void Behave(string value, Func<ConcurrentProbeCommand, CancellationToken, Task<Result>> behaviour) =>
        _behaviours[value] = behaviour;

    /// <summary>
    /// Holds the row whose value is <paramref name="value"/> inside its handler until the returned source
    /// is completed with the outcome. The wait observes the execution's token.
    /// </summary>
    /// <param name="value">The row's value.</param>
    /// <returns>The source the test completes to release the row.</returns>
    public TaskCompletionSource<Result> Hold(string value)
    {
        var release = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[value] = release;
        Behave(value, (_, ct) => release.Task.WaitAsync(ct));
        return release;
    }

    /// <summary>Completes every held row that is still held with success.</summary>
    public void ReleaseAll()
    {
        foreach (var release in _held.Values)
        {
            release.TrySetResult(Result.Success());
        }
    }

    /// <summary>Completes once <paramref name="count"/> invocations have started.</summary>
    /// <param name="count">The number of starts to wait for.</param>
    /// <returns>A task that completes when the count is reached.</returns>
    public Task WhenStartedCountAsync(int count) => WhenAsync(() => _invocations.Count >= count);

    /// <summary>Completes once the handler for <paramref name="value"/> has returned or thrown.</summary>
    /// <param name="value">The row's value.</param>
    /// <returns>A task that completes when it has finished.</returns>
    public Task WhenFinishedAsync(string value) =>
        WhenAsync(() => _finished.Contains(value, StringComparer.Ordinal));

    /// <summary>Counts one execution scope.</summary>
    public void CountExecutionScope() => Interlocked.Increment(ref _executionScopes);

    /// <summary>Records the invocation and runs the row's behaviour.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">The execution's token.</param>
    /// <returns>The behaviour's result.</returns>
    public async Task<Result> InvokeAsync(ConcurrentProbeCommand command, CancellationToken cancellationToken)
    {
        var receiverInFlight = _inFlightPerReceiver.AddOrUpdate(command.Receiver, 1, (_, current) => current + 1);
        RaiseMax(ref _maxInFlightPerReceiver, receiverInFlight);
        RaiseMax(ref _maxInFlight, Interlocked.Increment(ref _inFlight));

        lock (_gate)
        {
            _invocations.Add(new ConcurrentProbeInvocation(command.Receiver, command.Value, clock.GetUtcNow()));
        }

        Changed();

        try
        {
            return _behaviours.TryGetValue(command.Value, out var behaviour)
                ? await behaviour(command, cancellationToken).ConfigureAwait(false)
                : Result.Success();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _inFlightPerReceiver.AddOrUpdate(command.Receiver, 0, (_, current) => current - 1);
            lock (_gate)
            {
                _finished.Add(command.Value);
            }

            Changed();
        }
    }

    private static void RaiseMax(ref int max, int candidate)
    {
        var observed = Volatile.Read(ref max);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref max, candidate, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    private Task WhenAsync(Func<bool> condition)
    {
        lock (_gate)
        {
            if (condition())
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((condition, reached));
            return reached.Task;
        }
    }

    private void Changed()
    {
        lock (_gate)
        {
            foreach (var waiter in _waiters.Where(w => w.Condition()).ToList())
            {
                waiter.Reached.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
    }
}

/// <summary>The handler the Phase 2 test lanes' rows execute through; it defers to <see cref="ConcurrentProbeLog"/>.</summary>
/// <param name="log">The shared probe log.</param>
internal sealed class ConcurrentProbeHandler(ConcurrentProbeLog log) : ICommandHandler<ConcurrentProbeCommand, Result>
{
    public Task<Result> HandleAsync(ConcurrentProbeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return log.InvokeAsync(command, cancellationToken);
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports every timer created on it, so a test can tell that a
/// background loop has reached a wait (it creates the wait's timer) before it advances the clock past it.
/// Without this a test cannot know whether its <c>Advance</c> landed before or after the loop's
/// <c>Task.Delay</c> registered, and a delay registered after the advance would never fire.
/// </summary>
/// <param name="start">The starting instant.</param>
internal sealed class TimerWatchingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<TimeSpan> _created = [];
    private readonly List<(Func<IReadOnlyList<TimeSpan>, bool> Condition, TaskCompletionSource Reached)> _waiters = [];

    /// <summary>Gets the due time of every timer created so far, in order.</summary>
    public IReadOnlyList<TimeSpan> CreatedTimers
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    /// <summary>Completes once <paramref name="count"/> timers with due time <paramref name="dueTime"/> have been created.</summary>
    /// <param name="dueTime">The due time to match.</param>
    /// <param name="count">How many such timers to wait for.</param>
    /// <returns>A task that completes when they exist.</returns>
    public Task WhenTimersCreatedAsync(TimeSpan dueTime, int count)
    {
        bool Reached(IReadOnlyList<TimeSpan> created) => created.Count(t => t == dueTime) >= count;

        lock (_gate)
        {
            if (Reached(_created))
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((Reached, reached));
            return reached.Task;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _created.Add(dueTime);
            foreach (var waiter in _waiters.Where(w => w.Condition(_created)).ToList())
            {
                waiter.Reached.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        return timer;
    }
}
