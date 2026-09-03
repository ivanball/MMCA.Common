using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// Coverage for <see cref="RefreshSessionCleanupService"/>: the two disabled paths, the retention
/// semantics (a row is a candidate once it stopped being usable, not once it was created), the
/// per-sweep count line the runbook reads, and the not-mapped guard. The sweep is driven
/// deterministically through the service's <see cref="TimeProvider"/> extension point over an
/// in-memory SQLite context, following the <c>OutboxCleanupServiceTests</c> harness pattern.
/// </summary>
public sealed class RefreshSessionCleanupServiceTests
{
    // LoggerMessage event names (the source generator names the EventId after the logging method).
    private const string PurgedEvent = "LogPurged";
    private const string SessionsDisabledEvent = "LogSessionsDisabled";
    private const string CleanupDisabledEvent = "LogCleanupDisabled";
    private const string TableNotMappedEvent = "LogTableNotMapped";

    private const UserIdentifierType UserId = 42;

    private static readonly DateTimeOffset SweepStart = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

    // ── Disabled paths ──
    [Fact]
    public async Task ExecuteAsync_WhenSessionsAreDisabled_ExitsWithoutTouchingAnyDatabase()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var logger = CreateLogger();
        using var sut = new RefreshSessionCleanupService(
            scopeFactory.Object,
            logger.Object,
            Options.Create(new RefreshSessionSettings { Enabled = false }));

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
        VerifyLogged(logger, LogLevel.Information, SessionsDisabledEvent, Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_WhenRetentionDaysIsZero_LogsDisabledAndNeverSweeps()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var logger = CreateLogger();
        using var sut = new RefreshSessionCleanupService(
            scopeFactory.Object,
            logger.Object,
            Options.Create(new RefreshSessionSettings { Enabled = true, RetentionDays = 0 }));

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
        VerifyLogged(logger, LogLevel.Information, CleanupDisabledEvent, Times.Once());
    }

    [Fact]
    public async Task StopAsync_DuringTheInitialIntervalWait_ShutsDownWithoutSweeping()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        using var sut = new RefreshSessionCleanupService(
            scopeFactory.Object,
            CreateLogger().Object,
            Options.Create(new RefreshSessionSettings { Enabled = true, CleanupIntervalHours = 1 }));

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse();
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // ── Retention semantics ──
    [Fact]
    public async Task PurgeSweep_DeletesLongRevokedAndLongExpiredRows_KeepsLiveAndRecentlyDead()
    {
        await using var harness = await SweepHarness.CreateAsync();

        RefreshSession longRevoked = harness.Seed("a", createdAt: Days(-90), revokedAt: Days(-31));
        RefreshSession longExpired = harness.Seed("b", createdAt: Days(-90), expiresAt: Days(-31));
        RefreshSession live = harness.Seed("c", createdAt: Days(-1), expiresAt: Days(6));
        RefreshSession recentlyRevoked = harness.Seed("d", createdAt: Days(-90), revokedAt: Days(-2));
        RefreshSession justExpired = harness.Seed("e", createdAt: Days(-40), expiresAt: Days(-29));
        await harness.SaveAsync();

        await harness.SweepAsync(new RefreshSessionSettings { Enabled = true, RetentionDays = 30, CleanupIntervalHours = 1 });

        List<Guid> remaining = await harness.RemainingIdsAsync();
        remaining.Should().BeEquivalentTo(
            [live.Id, recentlyRevoked.Id, justExpired.Id],
            "a row ages from the instant it stopped being usable, so a live session and anything that died inside the window survive");
        remaining.Should().NotContain([longRevoked.Id, longExpired.Id]);
    }

    [Fact]
    public async Task PurgeSweep_MeasuresARevokedRowFromItsRevocationNotItsExpiry()
    {
        await using var harness = await SweepHarness.CreateAsync();

        // Expired 89 days ago but revoked yesterday: the recent revocation is the BR-206 reuse
        // signal, so the row has to survive a 30-day window.
        RefreshSession revokedYesterday = harness.Seed("a", createdAt: Days(-95), expiresAt: Days(-89), revokedAt: Days(-1));
        await harness.SaveAsync();

        await harness.SweepAsync(new RefreshSessionSettings { Enabled = true, RetentionDays = 30, CleanupIntervalHours = 1 });

        (await harness.RemainingIdsAsync()).Should().Equal(revokedYesterday.Id);
    }

    [Fact]
    public async Task PurgeSweep_RespectsAShorterWindow()
    {
        await using var harness = await SweepHarness.CreateAsync();

        harness.Seed("a", createdAt: Days(-40), revokedAt: Days(-8));
        RefreshSession dead2Days = harness.Seed("b", createdAt: Days(-40), revokedAt: Days(-2));
        await harness.SaveAsync();

        await harness.SweepAsync(new RefreshSessionSettings { Enabled = true, RetentionDays = 7, CleanupIntervalHours = 1 });

        (await harness.RemainingIdsAsync()).Should().BeEquivalentTo(
            [dead2Days.Id],
            "the window is what decides, and only the row past it goes");
    }

    [Fact]
    public async Task PurgeSweep_LogsTheDeletedCountEvenWhenNothingWasDeleted()
    {
        await using var harness = await SweepHarness.CreateAsync();
        harness.Seed("a", createdAt: Days(-1), expiresAt: Days(6));
        await harness.SaveAsync();

        await harness.SweepAsync(new RefreshSessionSettings { Enabled = true, RetentionDays = 30, CleanupIntervalHours = 1 });

        VerifyLogged(harness.Logger, LogLevel.Information, PurgedEvent, Times.AtLeastOnce(),
            "the per-sweep count line is how an operator sees retention is running at all");
    }

    // ── Wrong database ──
    [Fact]
    public async Task PurgeSweep_WhenTheSourceDoesNotMapTheTable_WarnsInsteadOfThrowing()
    {
        await using var harness = await SweepHarness.CreateAsync(mapSessions: false);

        await harness.SweepAsync(
            new RefreshSessionSettings { Enabled = true, RetentionDays = 30, CleanupIntervalHours = 1 },
            observedLogEvent: TableNotMappedEvent);

        VerifyLogged(harness.Logger, LogLevel.Warning, TableNotMappedEvent, Times.AtLeastOnce());
    }

    // ── Registration ──
    [Fact]
    public void AddInfrastructure_WithSessionsDisabled_DoesNotRegisterTheSweep()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(CreateConfiguration(sessionsEnabled: false));

        services.Should().NotContain(
            d => d.ImplementationType == typeof(RefreshSessionCleanupService),
            "a service that never mapped the table must not start an hourly sweep over it");
    }

    [Fact]
    public void AddInfrastructure_WithSessionsEnabled_RegistersTheSweepAsAHostedService()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(CreateConfiguration(sessionsEnabled: true));

        services.Should().ContainSingle(
            d => d.ImplementationType == typeof(RefreshSessionCleanupService)
                && d.ServiceType == typeof(IHostedService)
                && d.Lifetime == ServiceLifetime.Singleton);
    }

    // ── Source resolution ──
    [Fact]
    public void ResolveDataSourceKey_WithNoRegistryEntry_FallsBackToTheConfiguredSource()
    {
        using var sut = new RefreshSessionCleanupService(
            new Mock<IServiceScopeFactory>().Object,
            CreateLogger().Object,
            Options.Create(new RefreshSessionSettings { Enabled = true, DataSourceName = "Identity" }));

        DataSourceKey key = InvokeResolveDataSourceKey(sut, new EmptyEntityDataSourceRegistry());

        key.Should().Be(new DataSourceKey(DataSource.SQLServer, "Identity"),
            "the sweep must land on the same database EFRefreshSessionStore reads");
    }

    [Fact]
    public void ResolveDataSourceKey_WithARegistryEntry_UsesIt()
    {
        using var sut = new RefreshSessionCleanupService(
            new Mock<IServiceScopeFactory>().Object,
            CreateLogger().Object,
            Options.Create(new RefreshSessionSettings { Enabled = true, DataSourceName = "Identity" }));

        var registered = new DataSourceKey(DataSource.Sqlite, "Registered");
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry
            .Setup(r => r.TryGetDataSourceKey(typeof(RefreshSession).FullName!, out registered))
            .Returns(true);

        DataSourceKey key = InvokeResolveDataSourceKey(sut, registry.Object);

        key.Should().Be(registered, "a consumer that ships its own entity configuration routes the table itself");
    }

    // ── Helpers ──
    private static DateTime Days(int offset) => SweepStart.UtcDateTime.AddDays(offset);

    private static IConfiguration CreateConfiguration(bool sessionsEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=test;Database=test",
                ["Jwt:SecretForKey"] = "dGVzdGtleXRoYXRpc2xvbmdlbm91Z2hmb3JiYXNlNjQ=",
                ["Jwt:Issuer"] = "https://test",
                ["Jwt:Audience"] = "test",
                ["Outbox:DataSource"] = "SQLServer",
                ["RefreshSessions:Enabled"] = sessionsEnabled ? "true" : "false",
            })
            .Build();

    private static Mock<ILogger<RefreshSessionCleanupService>> CreateLogger()
    {
        var logger = new Mock<ILogger<RefreshSessionCleanupService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return logger;
    }

    private static void VerifyLogged(
        Mock<ILogger<RefreshSessionCleanupService>> logger,
        LogLevel level,
        string eventName,
        Times times,
        string? because = null) =>
        logger.Verify(
            l => l.Log(
                level,
                It.Is<EventId>(e => e.Name == eventName),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times,
            because ?? string.Empty);

    /// <summary>
    /// Calls the private source resolution through reflection, mirroring the existing non-public
    /// member test pattern in this project (<c>OutboxCleanupServiceTests</c>).
    /// </summary>
    private static DataSourceKey InvokeResolveDataSourceKey(
        RefreshSessionCleanupService sut,
        IEntityDataSourceRegistry registry,
        IDataSourceResolver? resolver = null)
    {
        var method = typeof(RefreshSessionCleanupService)
            .GetMethod("ResolveDataSourceKey", BindingFlags.Instance | BindingFlags.NonPublic);

        return (DataSourceKey)method!.Invoke(sut, [registry, resolver ?? new DefaultDataSourceResolver()])!;
    }

    /// <summary>
    /// A real sweep: a real <see cref="IServiceScopeFactory"/> over a mocked
    /// <see cref="IDbContextFactory"/> and registry, an in-memory SQLite context, and a
    /// <see cref="FakeTimeProvider"/> so the hour-scale loop is deterministic.
    /// </summary>
    private sealed class SweepHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _scopeServices;

        private SweepHarness(
            SqliteConnection connection,
            ApplicationDbContext context,
            ServiceProvider scopeServices)
        {
            _connection = connection;
            _scopeServices = scopeServices;
            Context = context;
            TimeProvider = new FakeTimeProvider(SweepStart);
            Logger = CreateLogger();
        }

        public ApplicationDbContext Context { get; }

        public FakeTimeProvider TimeProvider { get; }

        public Mock<ILogger<RefreshSessionCleanupService>> Logger { get; }

        public static async Task<SweepHarness> CreateAsync(bool mapSessions = true)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(CancellationToken.None);
            ApplicationDbContext context = mapSessions
                ? SessionCleanupTestContext.Create(connection)
                : NoSessionTableContext.Create(connection);

            var dbContextFactory = new Mock<IDbContextFactory>();
            dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);

            var services = new ServiceCollection();
            services.AddScoped(_ => dbContextFactory.Object);
            services.AddScoped<IEntityDataSourceRegistry>(_ => new EmptyEntityDataSourceRegistry());
            services.AddScoped<IDataSourceResolver>(_ => new DefaultDataSourceResolver());

            return new SweepHarness(connection, context, services.BuildServiceProvider());
        }

        public RefreshSession Seed(
            string token,
            DateTime createdAt,
            DateTime? expiresAt = null,
            DateTime? revokedAt = null)
        {
            RefreshSession session = RefreshSession.Create(
                UserId,
                token,
                createdAt,
                expiresAt ?? createdAt.AddDays(7)).Value!;

            if (revokedAt is { } revoked)
            {
                session.Revoke(revoked, RefreshSession.ReasonSignedOut);
            }

            Context.Add(session);
            return session;
        }

        public Task<int> SaveAsync() => Context.SaveChangesAsync(CancellationToken.None);

        public async Task<List<Guid>> RemainingIdsAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.Set<RefreshSession>().AsNoTracking()
                .Select(s => s.Id)
                .ToListAsync(CancellationToken.None);
        }

        /// <summary>Runs the service until <paramref name="observedLogEvent"/> fires, then stops it.</summary>
        public async Task SweepAsync(RefreshSessionSettings settings, string observedLogEvent = PurgedEvent)
        {
            var sweepObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Logger
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

            using var service = new RefreshSessionCleanupService(
                _scopeServices.GetRequiredService<IServiceScopeFactory>(),
                Logger.Object,
                Options.Create(settings),
                TimeProvider);

            await service.StartAsync(CancellationToken.None);

            var interval = TimeSpan.FromHours(settings.CleanupIntervalHours);
            for (var i = 0; i < 100 && !sweepObserved.Task.IsCompleted; i++)
            {
                TimeProvider.Advance(interval);

                // A REAL (system-clock) yield so the awoken sweep can run; the fake provider in scope
                // must not be used here or the wait itself would need advancing.
                await Task.Delay(TimeSpan.FromMilliseconds(10), System.TimeProvider.System, CancellationToken.None);
            }

            await sweepObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), System.TimeProvider.System);
            await service.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _scopeServices.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>The context of a database that maps the session table: the ordinary case.</summary>
    internal sealed class SessionCleanupTestContext : ApplicationDbContext
    {
        private SessionCleanupTestContext(DbContextOptions<SessionCleanupTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static SessionCleanupTestContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<SessionCleanupTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new SessionCleanupTestContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

        // No schema: SQLite has none, and the sweep is about rows, not about placement.
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyRefreshSessionConfiguration(schema: null);
    }

    /// <summary>
    /// The context of a database that holds other framework tables but never mapped the session
    /// table: the wrong-database case the sweep has to report rather than fail on.
    /// </summary>
    /// <remarks>
    /// It is a separate context CLASS, not a flag on the one above, because EF's model cache is keyed
    /// on (context type, source name): two shapes behind one type would silently share whichever
    /// model was built first, and the guard would then be asserted against the wrong model.
    /// </remarks>
    internal sealed class NoSessionTableContext : ApplicationDbContext
    {
        private NoSessionTableContext(DbContextOptions<NoSessionTableContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static NoSessionTableContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<NoSessionTableContext>()
                .UseSqlite(connection)
                .Options;

            var context = new NoSessionTableContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
            });
    }

    /// <summary>The minimal service graph an <c>ApplicationDbContext</c> resolves at construction.</summary>
    private static ServiceProvider BuildContextServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditSaveChangesInterceptor(System.TimeProvider.System));
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

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
