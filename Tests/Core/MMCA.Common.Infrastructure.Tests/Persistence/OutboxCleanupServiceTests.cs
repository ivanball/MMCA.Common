using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Coverage for <see cref="OutboxCleanupService"/>: the RetentionDays-disabled path,
/// immediate-shutdown behavior, the private relational-source selection helper (via
/// reflection, mirroring the existing non-public-member test pattern in this project),
/// and the purge sweep itself, driven deterministically through the service's
/// <see cref="TimeProvider"/> seam with a <see cref="FakeTimeProvider"/> over an in-memory
/// SQLite <see cref="ApplicationDbContext"/> (the BrokerEventBusTests harness pattern).
/// </summary>
public sealed class OutboxCleanupServiceTests
{
    // LoggerMessage event names (the source generator names the EventId after the logging method).
    // The sweep tests use them both to assert specific log entries and as completion signals.
    private const string PurgedEvent = "LogPurged";
    private const string DeadLetterPurgedEvent = "LogDeadLetterPurged";
    private const string InboxPurgedEvent = "LogInboxPurged";
    private const string SourceErrorEvent = "LogSourcePurgeError";

    // FakeTimeProvider epoch, passed explicitly so seeded timestamps are self-explanatory.
    private static readonly DateTimeOffset SweepStart = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Mocks ──
    private sealed record Mocks(
        Mock<IServiceScopeFactory> ScopeFactory,
        Mock<ILogger<OutboxCleanupService>> Logger,
        Mock<IEntityDataSourceRegistry> Registry,
        Mock<IDataSourceResolver> Resolver);

    // ── Factory ──
    private static (OutboxCleanupService Sut, Mocks Mocks) CreateSut(
        OutboxSettings outboxSettings,
        MessageBusSettings? messageBusSettings = null)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();

        var logger = new Mock<ILogger<OutboxCleanupService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        var sut = new OutboxCleanupService(
            scopeFactory.Object,
            logger.Object,
            Options.Create(outboxSettings),
            Options.Create(messageBusSettings ?? new MessageBusSettings()),
            registry.Object,
            resolver.Object);

        return (sut, new Mocks(scopeFactory, logger, registry, resolver));
    }

    // ── Disabled path: RetentionDays 0 logs and exits without touching the store ──
    [Fact]
    public async Task ExecuteAsync_RetentionDaysZero_LogsDisabledAndNeverTouchesTheStore()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 0 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue(
            "the disabled path should return immediately without faulting");
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
        mocks.Registry.Verify(r => r.GetPhysicalSourcesInUse(), Times.Never);
        // The LoggerMessage source generator names the EventId after the logging method, and its
        // LoggerMessageState does not expose the formatted message via ToString(), so the event
        // id is the stable way to assert THIS specific log entry was written.
        mocks.Logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.Is<EventId>(e => e.Name == "LogCleanupDisabled"),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── Disabled path: a negative retention behaves like 0 (guard is <= 0) ──
    [Fact]
    public async Task ExecuteAsync_NegativeRetentionDays_ExitsWithoutPurging()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = -1 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // ── Shutdown: StopAsync during the initial interval wait completes promptly, no sweep ──
    [Fact]
    public async Task StopAsync_DuringInitialIntervalWait_ShutsDownPromptlyWithoutSweeping()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        service.ExecuteTask!.IsCompleted.Should().BeFalse(
            "the service should be waiting out the first cleanup interval, not running or finished");

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        // .NET 10's BackgroundService dispatches ExecuteAsync via Task.Run(..., stoppingToken), so a
        // stop that wins the race against the thread pool cancels ExecuteTask outright before
        // ExecuteAsync ever runs (task ends Canceled, not RanToCompletion). Both outcomes are a
        // graceful prompt shutdown; the contract to pin is completed-without-fault and no sweep.
        service.ExecuteTask.IsCompleted.Should().BeTrue("StopAsync must not return before the execute task settles");
        service.ExecuteTask.IsFaulted.Should().BeFalse(
            "cancellation during the interval wait must exit the loop gracefully, not fault");
        mocks.ScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "no purge sweep should run when the service is stopped before the first interval elapses");
    }

    // ── Shutdown: cancelling the start token during the interval wait also exits gracefully ──
    [Fact]
    public async Task ExecuteAsync_StartTokenCancelledDuringIntervalWait_ExitsGracefully()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1 });
        using var service = sut;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.StartAsync(cts.Token);
        try
        {
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // .NET 10's BackgroundService dispatches ExecuteAsync via Task.Run(..., stoppingToken):
            // when the 100ms token fires before the pool runs the work item, ExecuteTask ends
            // Canceled without ExecuteAsync ever starting. That is still a graceful non-faulted exit.
        }

        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask.IsFaulted.Should().BeFalse(
            "cancelling the start token during the interval wait must exit gracefully, not fault");
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // ── Source selection: Cosmos sources are excluded, the publish target is appended ──
    [Fact]
    public void GetRelationalSources_ExcludesCosmosSourcesAndAppendsPublishTarget()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings
        {
            DataSource = DataSource.SQLServer,
            DatabaseName = "Outbox",
        });
        using var service = sut;
        var conference = new DataSourceKey(DataSource.SQLServer, "Conference");
        var engagement = new DataSourceKey(DataSource.CosmosDB, "Engagement");
        var publishTarget = new DataSourceKey(DataSource.SQLServer, "Outbox");
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([conference, engagement]);
        mocks.Resolver
            .Setup(r => r.ResolveLogical(DataSource.SQLServer, "Outbox"))
            .Returns(publishTarget);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(conference, publishTarget);
    }

    // ── Source selection: a Cosmos publish target is never appended (and never resolved) ──
    [Fact]
    public void GetRelationalSources_CosmosPublishTarget_DoesNotAppendTarget()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { DataSource = DataSource.CosmosDB });
        using var service = sut;
        var conference = new DataSourceKey(DataSource.SQLServer, "Conference");
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([conference]);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(conference);
        mocks.Resolver.Verify(
            r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Source selection: a registry source equal to the publish target is deduplicated ──
    [Fact]
    public void GetRelationalSources_PublishTargetAlreadyInRegistry_IsDeduplicated()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { DataSource = DataSource.SQLServer });
        using var service = sut;
        var defaultSqlServer = DataSourceKey.Default(DataSource.SQLServer);
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([defaultSqlServer]);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(defaultSqlServer);
    }

    // ── Purge sweep: processed rows older than the retention cutoff are deleted ──
    [Fact]
    public async Task PurgeSweep_DeletesProcessedRowsOlderThanRetention_KeepsRecentAndPending()
    {
        var timeProvider = new FakeTimeProvider(SweepStart);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CleanupTestContext.Create(connection);

        var oldProcessed = ProcessedOutboxMessage(SweepStart.UtcDateTime.AddDays(-8));
        var recentProcessed = ProcessedOutboxMessage(SweepStart.UtcDateTime.AddDays(-1));
        var pending = PendingOutboxMessage(SweepStart.UtcDateTime.AddDays(-30));
        context.AddRange(oldProcessed, recentProcessed, pending);
        await context.SaveChangesAsync(CancellationToken.None);

        var (sut, sweepObserved, _, scopeServices) = CreateSweepSut(
            timeProvider,
            _ => context,
            [DataSourceKey.Default(DataSource.Sqlite)],
            new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1, DataSource = DataSource.Sqlite },
            observedLogEvent: PurgedEvent);
        await using var scope = scopeServices;
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilSweepAsync(timeProvider, TimeSpan.FromHours(1), sweepObserved);
        await service.StopAsync(CancellationToken.None);

        List<Guid> remaining = await context.Set<OutboxMessage>().AsNoTracking()
            .Select(m => m.Id)
            .ToListAsync(CancellationToken.None);
        remaining.Should().BeEquivalentTo(
            [recentProcessed.Id, pending.Id],
            "only PROCESSED rows older than the retention cutoff are purged; newer processed rows and pending rows survive");
    }

    // ── Purge sweep: dead-lettered rows (retries exhausted) older than retention are purged ──
    [Fact]
    public async Task PurgeSweep_DeletesDeadLetteredRowsOlderThanRetention_KeepsRecentAndStillRetrying()
    {
        var timeProvider = new FakeTimeProvider(SweepStart);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CleanupTestContext.Create(connection);

        // MaxRetries 3: RetryCount >= 3 is dead-lettered; RetryCount 1 is still eligible to retry.
        var oldDeadLettered = DeadLetteredOutboxMessage(SweepStart.UtcDateTime.AddDays(-8), retryCount: 3);
        var recentDeadLettered = DeadLetteredOutboxMessage(SweepStart.UtcDateTime.AddDays(-1), retryCount: 3);
        var oldStillRetrying = PendingWithRetries(SweepStart.UtcDateTime.AddDays(-30), retryCount: 1);
        context.AddRange(oldDeadLettered, recentDeadLettered, oldStillRetrying);
        await context.SaveChangesAsync(CancellationToken.None);

        var (sut, sweepObserved, _, scopeServices) = CreateSweepSut(
            timeProvider,
            _ => context,
            [DataSourceKey.Default(DataSource.Sqlite)],
            new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1, MaxRetries = 3, DataSource = DataSource.Sqlite },
            observedLogEvent: DeadLetterPurgedEvent);
        await using var scope = scopeServices;
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilSweepAsync(timeProvider, TimeSpan.FromHours(1), sweepObserved);
        await service.StopAsync(CancellationToken.None);

        List<Guid> remaining = await context.Set<OutboxMessage>().AsNoTracking()
            .Select(m => m.Id)
            .ToListAsync(CancellationToken.None);
        remaining.Should().BeEquivalentTo(
            [recentDeadLettered.Id, oldStillRetrying.Id],
            "only dead-lettered rows older than retention are purged; recent dead rows and still-retrying rows survive");
    }

    // ── Purge sweep: one unreachable source must not stop the others from being purged ──
    [Fact]
    public async Task PurgeSweep_WhenOneSourceThrows_StillPurgesTheOtherSources()
    {
        var timeProvider = new FakeTimeProvider(SweepStart);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CleanupTestContext.Create(connection);

        var oldProcessed = ProcessedOutboxMessage(SweepStart.UtcDateTime.AddDays(-8));
        context.Add(oldProcessed);
        await context.SaveChangesAsync(CancellationToken.None);

        var broken = new DataSourceKey(DataSource.Sqlite, "Broken");
        var (sut, sweepObserved, logger, scopeServices) = CreateSweepSut(
            timeProvider,
            key => key == broken ? throw new InvalidOperationException("unreachable database") : context,
            [broken, DataSourceKey.Default(DataSource.Sqlite)],
            new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1, DataSource = DataSource.Sqlite },
            observedLogEvent: PurgedEvent);
        await using var scope = scopeServices;
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilSweepAsync(timeProvider, TimeSpan.FromHours(1), sweepObserved);
        await service.StopAsync(CancellationToken.None);

        (await context.Set<OutboxMessage>().AsNoTracking().CountAsync(CancellationToken.None)).Should().Be(
            0, "the healthy source is purged even though an earlier source failed");
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.Is<EventId>(e => e.Name == SourceErrorEvent),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "the unreachable source is reported per-source instead of aborting the sweep");
    }

    // ── Purge sweep: inbox purge is gated on MessageBus:EnableInbox (off by default) ──
    [Fact]
    public async Task PurgeSweep_InboxDisabled_LeavesOldInboxRowsUntouched()
    {
        var timeProvider = new FakeTimeProvider(SweepStart);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CleanupTestContext.Create(connection);

        var oldProcessed = ProcessedOutboxMessage(SweepStart.UtcDateTime.AddDays(-8));
        var oldInbox = InboxMessageProcessedOn(SweepStart.UtcDateTime.AddDays(-8));
        context.AddRange(oldProcessed, oldInbox);
        await context.SaveChangesAsync(CancellationToken.None);

        var (sut, sweepObserved, logger, scopeServices) = CreateSweepSut(
            timeProvider,
            _ => context,
            [DataSourceKey.Default(DataSource.Sqlite)],
            new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1, DataSource = DataSource.Sqlite },
            observedLogEvent: PurgedEvent);
        await using var scope = scopeServices;
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilSweepAsync(timeProvider, TimeSpan.FromHours(1), sweepObserved);
        await service.StopAsync(CancellationToken.None);

        (await context.Set<OutboxMessage>().AsNoTracking().CountAsync(CancellationToken.None)).Should().Be(0);
        (await context.Set<InboxMessage>().AsNoTracking().CountAsync(CancellationToken.None)).Should().Be(
            1, "inbox purging must not run while MessageBus:EnableInbox is false");
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.Is<EventId>(e => e.Name == InboxPurgedEvent),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    // ── Purge sweep: with the inbox enabled, old inbox rows are purged and recent ones survive ──
    [Fact]
    public async Task PurgeSweep_InboxEnabled_PurgesOldInboxRowsAndKeepsRecent()
    {
        var timeProvider = new FakeTimeProvider(SweepStart);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CleanupTestContext.Create(connection);

        var oldInbox = InboxMessageProcessedOn(SweepStart.UtcDateTime.AddDays(-8));
        var recentInbox = InboxMessageProcessedOn(SweepStart.UtcDateTime.AddDays(-1));
        context.AddRange(oldInbox, recentInbox);
        await context.SaveChangesAsync(CancellationToken.None);

        var (sut, sweepObserved, _, scopeServices) = CreateSweepSut(
            timeProvider,
            _ => context,
            [DataSourceKey.Default(DataSource.Sqlite)],
            new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1, DataSource = DataSource.Sqlite },
            observedLogEvent: InboxPurgedEvent,
            new MessageBusSettings { EnableInbox = true });
        await using var scope = scopeServices;
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilSweepAsync(timeProvider, TimeSpan.FromHours(1), sweepObserved);
        await service.StopAsync(CancellationToken.None);

        List<Guid> remaining = await context.Set<InboxMessage>().AsNoTracking()
            .Select(m => m.Id)
            .ToListAsync(CancellationToken.None);
        remaining.Should().BeEquivalentTo(
            [recentInbox.Id],
            "inbox rows processed before the retention cutoff are purged; newer rows survive");
    }

    // ── Helpers ──

    /// <summary>
    /// Invokes the private <c>GetRelationalSources</c> helper via reflection so the pure
    /// source-selection logic can be verified without driving the hour-scale purge loop.
    /// </summary>
    private static List<DataSourceKey> InvokeGetRelationalSources(OutboxCleanupService sut)
    {
        MethodInfo? method = typeof(OutboxCleanupService)
            .GetMethod("GetRelationalSources", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        return (List<DataSourceKey>)method!.Invoke(sut, [])!;
    }

    // ── Sweep-test harness ──

    /// <summary>
    /// Builds an <see cref="OutboxCleanupService"/> wired for a real sweep: a real
    /// <see cref="IServiceScopeFactory"/> resolving a mocked <see cref="IDbContextFactory"/>
    /// (routing each <see cref="DataSourceKey"/> through <paramref name="contextForSource"/>),
    /// the <see cref="FakeTimeProvider"/> seam, and a logger whose <paramref name="observedLogEvent"/>
    /// completes the returned task so tests can await the sweep deterministically.
    /// </summary>
    private static (OutboxCleanupService Sut,
        Task SweepObserved,
        Mock<ILogger<OutboxCleanupService>> Logger,
        ServiceProvider ScopeServices) CreateSweepSut(
            FakeTimeProvider timeProvider,
            Func<DataSourceKey, ApplicationDbContext> contextForSource,
            IReadOnlyCollection<DataSourceKey> registrySources,
            OutboxSettings settings,
            string observedLogEvent,
            MessageBusSettings? messageBusSettings = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns((DataSourceKey key) => contextForSource(key));

        var services = new ServiceCollection();
        services.AddScoped(_ => dbContextFactory.Object);
        var scopeServices = services.BuildServiceProvider();

        var sweepObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger<OutboxCleanupService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                if (string.Equals(((EventId)invocation.Arguments[1]).Name, observedLogEvent, StringComparison.Ordinal))
                {
                    sweepObserved.TrySetResult();
                }
            }));

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns(registrySources);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        var sut = new OutboxCleanupService(
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            logger.Object,
            Options.Create(settings),
            Options.Create(messageBusSettings ?? new MessageBusSettings()),
            registry.Object,
            resolver.Object,
            timeProvider);

        return (sut, sweepObserved.Task, logger, scopeServices);
    }

    /// <summary>
    /// Advances the fake clock one cleanup interval at a time, yielding a few real milliseconds after
    /// each advance so the awoken sweep can run, until the observed log event fires.
    /// <see cref="FakeTimeProvider.Advance"/> only completes a Delay whose timer already exists, so the
    /// loop tolerates the startup race between <c>StartAsync</c> and the first Delay registration. The
    /// iteration cap both fails a broken sweep fast and keeps cumulative fake time far below the
    /// retention window of the seeded "survivor" rows.
    /// </summary>
    private static async Task AdvanceUntilSweepAsync(FakeTimeProvider timeProvider, TimeSpan interval, Task sweepObserved)
    {
        for (var i = 0; i < 100 && !sweepObserved.IsCompleted; i++)
        {
            timeProvider.Advance(interval);

            // A REAL (system-clock) yield so the sweep continuation can run; the fake provider in
            // scope must not be used here or the wait itself would need advancing.
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None);
        }

        await sweepObserved.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
    }

    private static OutboxMessage ProcessedOutboxMessage(DateTime processedOn) => new()
    {
        EventType = "Test.Event",
        Payload = "{}",
        OccurredOn = processedOn.AddMinutes(-1),
        ProcessedOn = processedOn,
    };

    private static OutboxMessage PendingOutboxMessage(DateTime occurredOn) => new()
    {
        EventType = "Test.Event",
        Payload = "{}",
        OccurredOn = occurredOn,
    };

    private static OutboxMessage DeadLetteredOutboxMessage(DateTime occurredOn, int retryCount) => new()
    {
        EventType = "Test.Event",
        Payload = "{}",
        OccurredOn = occurredOn,
        RetryCount = retryCount,
        LastError = "boom",
    };

    private static OutboxMessage PendingWithRetries(DateTime occurredOn, int retryCount) => new()
    {
        EventType = "Test.Event",
        Payload = "{}",
        OccurredOn = occurredOn,
        RetryCount = retryCount,
    };

    private static InboxMessage InboxMessageProcessedOn(DateTime processedOn) => new()
    {
        MessageId = Guid.NewGuid(),
        EventType = "Test.Event",
        ProcessedOn = processedOn,
    };

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> mapping both <see cref="OutboxMessage"/> and
    /// <see cref="InboxMessage"/> (the two tables the cleanup service sweeps), SQLite-portable,
    /// following the BrokerEventBusTests harness pattern.
    /// </summary>
    private sealed class CleanupTestContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private CleanupTestContext(DbContextOptions<CleanupTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static CleanupTestContext Create(SqliteConnection connection)
        {
            IServiceProvider sp = BuildContextServices();

            var options = new DbContextOptionsBuilder<CleanupTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new CleanupTestContext(options, sp);
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(4000);
            });
            modelBuilder.Entity<InboxMessage>(entity =>
            {
                entity.ToTable("InboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.HasIndex(e => e.MessageId).IsUnique();
            });
        }

        private static ServiceProvider BuildContextServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ =>
            {
                var dispatcher = new Mock<IDomainEventDispatcher>();
                var logger = new Mock<ILogger<DomainEventSaveChangesInterceptor>>();
                var outboxSignal = new Mock<IOutboxSignal>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
            });
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            return services.BuildServiceProvider();
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
