using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Scheduling;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Shared scaffolding for the scheduler tests: an in-memory SQLite
/// <see cref="ApplicationDbContext"/> mapping <see cref="ScheduledJobEntry"/> (the
/// OutboxCleanupServiceTests harness pattern) plus a <see cref="ScheduledJobRunner"/> wired to it
/// over a <see cref="FakeTimeProvider"/>.
/// </summary>
internal static class SchedulerTestHarness
{
    /// <summary>Epoch for every scheduler test, passed explicitly so seeded timestamps read plainly.</summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The <see cref="Epoch"/> as the UTC <see cref="DateTime"/> the runner works in.</summary>
    public static DateTime EpochUtc => Epoch.UtcDateTime;

    /// <summary>
    /// Builds a runner over <paramref name="context"/> with <paramref name="jobs"/> registered in the
    /// scope the runner creates each cycle.
    /// </summary>
    public static (ScheduledJobRunner Runner, ServiceProvider ScopeServices, Mock<ILogger<ScheduledJobRunner>> Logger) CreateRunner(
        ApplicationDbContext context,
        SchedulerSettings settings,
        FakeTimeProvider timeProvider,
        params IScheduledJob[] jobs)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>()))
            .Returns(context);

        var services = new ServiceCollection();
        services.AddScoped(_ => dbContextFactory.Object);
        foreach (var job in jobs)
        {
            services.AddScoped<IScheduledJob>(_ => job);
        }

        var scopeServices = services.BuildServiceProvider();

        var logger = new Mock<ILogger<ScheduledJobRunner>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        var runner = new ScheduledJobRunner(
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            logger.Object,
            Options.Create(settings),
            resolver.Object,
            timeProvider);

        return (runner, scopeServices, logger);
    }

    /// <summary>Scheduler settings with the SQLite harness selected and the scheduler switched on.</summary>
    public static SchedulerSettings EnabledSettings(int leaseSeconds = 300) => new()
    {
        Enabled = true,
        DataSource = DataSource.Sqlite,
        LeaseSeconds = leaseSeconds,
    };

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> mapping only <see cref="ScheduledJobEntry"/>,
    /// SQLite-portable (no schema, no SQL Server index annotations). The production gate that
    /// decides whether the table is in the model at all is covered separately by
    /// <c>SchedulerModelGateTests</c>, so this harness maps it unconditionally.
    /// </summary>
    public sealed class SchedulerTestContext : ApplicationDbContext
    {
        private SchedulerTestContext(DbContextOptions<SchedulerTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static SchedulerTestContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<SchedulerTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new SchedulerTestContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);
            modelBuilder.Entity<ScheduledJobEntry>(entity =>
            {
                entity.ToTable("ScheduledJobs");
                entity.HasKey(e => e.JobName);
                entity.Property(e => e.JobName).HasMaxLength(128);
                entity.Property(e => e.CronExpression).IsRequired().HasMaxLength(128);
                entity.Property(e => e.LastOutcome).HasMaxLength(32);
                entity.Property(e => e.LastError).HasMaxLength(2048);
                entity.HasIndex(e => e.NextRunOn);
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

/// <summary>
/// A job whose body is supplied by the test. The delegate receives the job instance so a test can
/// count executions or advance the fake clock from inside the run.
/// </summary>
internal sealed class DelegateScheduledJob(string name, string cronExpression, Func<CancellationToken, Task>? body = null)
    : IScheduledJob
{
    public string Name => name;

    public string CronExpression => cronExpression;

    /// <summary>Gets how many times this job has been executed.</summary>
    public int ExecutionCount { get; private set; }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ExecutionCount++;
        if (body is not null)
        {
            await body(cancellationToken);
        }
    }
}
