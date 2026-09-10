using System.Reflection;
using System.Security.Claims;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
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
/// Shared scaffolding for the internal-command tests: an in-memory SQLite
/// <see cref="ApplicationDbContext"/> mapping <see cref="InternalCommandMessage"/> (the
/// SchedulerTestHarness pattern), plus factories for a processor and a scheduler wired to it over a
/// <see cref="FakeTimeProvider"/>.
/// </summary>
internal static class InternalCommandTestHarness
{
    /// <summary>Epoch for every internal-command test, so seeded timestamps read plainly.</summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The <see cref="Epoch"/> as the UTC <see cref="DateTime"/> the processor works in.</summary>
    public static DateTime EpochUtc => Epoch.UtcDateTime;

    /// <summary>Queue settings pointing at the SQLite harness, with the grace period left at zero.</summary>
    public static InternalCommandsSettings Settings(
        int maxAttempts = 3,
        int batchSize = 50,
        int leaseSeconds = 300,
        int retryBackoffBaseSeconds = 10,
        int maxRetryBackoffSeconds = 600) => new()
        {
            Enabled = true,
            DataSource = DataSource.Sqlite,
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            LeaseSeconds = leaseSeconds,
            RetryBackoffBaseSeconds = retryBackoffBaseSeconds,
            MaxRetryBackoffSeconds = maxRetryBackoffSeconds,
        };

    /// <summary>
    /// Builds a processor over <paramref name="context"/>. The returned provider is what the
    /// processor creates its per-cycle and per-execution scopes from, so a test registers its
    /// handlers, validators and recording doubles through <paramref name="configure"/>.
    /// </summary>
    public static (InternalCommandProcessor Processor, ServiceProvider ScopeServices) CreateProcessor(
        ApplicationDbContext context,
        InternalCommandsSettings settings,
        FakeTimeProvider timeProvider,
        Action<IServiceCollection>? configure = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);

        var services = new ServiceCollection();
        services.AddScoped(_ => dbContextFactory.Object);
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        services.AddScoped<ScopedUserOverride>();
        services.AddScoped<ICurrentUserService>(sp => new ImpersonatingCurrentUserService(
            new AnonymousCurrentUserService(),
            sp.GetRequiredService<ScopedUserOverride>()));
        configure?.Invoke(services);

        var scopeServices = services.BuildServiceProvider();

        var processor = new InternalCommandProcessor(
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InternalCommandProcessor>.Instance,
            Options.Create(settings),
            Mock.Of<IInternalCommandSignal>(),
            new EmptyEntityDataSourceRegistry(),
            new DefaultDataSourceResolver(),
            timeProvider);

        return (processor, scopeServices);
    }

    /// <summary>
    /// Registers <see cref="RecordingCommand"/> behind a real
    /// <see cref="ValidatingCommandDecorator{TCommand, TResult}"/>, which is what lets a test prove
    /// the processor resolves the DECORATED registration rather than the bare handler.
    /// </summary>
    public static void AddRecordingCommandPipeline(IServiceCollection services, ExecutionLog log)
    {
        services.AddSingleton(log);
        services.AddScoped<RecordingCommandHandler>();
        services.AddScoped<IValidator<RecordingCommand>, RecordingCommandValidator>();
        services.AddScoped<ICommandHandler<RecordingCommand, Result>>(sp =>
            new ValidatingCommandDecorator<RecordingCommand, Result>(
                sp.GetRequiredService<RecordingCommandHandler>(),
                sp.GetServices<IValidator<RecordingCommand>>(),
                NullLogger<ValidatingCommandDecorator<RecordingCommand, Result>>.Instance));
    }

    /// <summary>Builds a scheduler writing to <paramref name="context"/>.</summary>
    public static InternalCommandScheduler CreateScheduler(
        ApplicationDbContext context,
        InternalCommandsSettings settings,
        FakeTimeProvider timeProvider,
        IInternalCommandSignal signal,
        ICurrentUserService? currentUser = null,
        ITenantContext? tenantContext = null)
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);

        return new InternalCommandScheduler(
            dbContextFactory.Object,
            new DefaultDataSourceResolver(),
            Options.Create(settings),
            currentUser ?? new AnonymousCurrentUserService(),
            tenantContext ?? new TenantContext(),
            new CorrelationContext(),
            signal,
            NullLogger<InternalCommandScheduler>.Instance,
            timeProvider);
    }

    /// <summary>Seeds one queued row directly, bypassing the scheduler.</summary>
    public static InternalCommandMessage Seed(
        ApplicationDbContext context,
        IInternalCommand command,
        DateTime scheduledOn,
        int attempts = 0,
        DateTime? claimedUntil = null,
        Guid? claimedBy = null)
    {
        var row = InternalCommandMessage.FromCommand(
            command, scheduledOn, scheduledOn, default);
        row.Attempts = attempts;
        row.ClaimedUntil = claimedUntil;
        row.ClaimedBy = claimedBy;
        context.Add(row);
        context.SaveChanges();
        return row;
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> mapping only <see cref="InternalCommandMessage"/>,
    /// SQLite-portable (no schema, no filtered index expressions). The production mapping is covered
    /// separately by <c>InternalCommandModelTests</c>.
    /// </summary>
    public sealed class InternalCommandTestContext : ApplicationDbContext
    {
        private InternalCommandTestContext(
            DbContextOptions<InternalCommandTestContext> options,
            IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static InternalCommandTestContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<InternalCommandTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new InternalCommandTestContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

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

        private static ServiceProvider BuildContextServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            return services.BuildServiceProvider();
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

/// <summary>An unauthenticated <see cref="ICurrentUserService"/> for tests.</summary>
internal sealed class AnonymousCurrentUserService : ICurrentUserService
{
    public ClaimsPrincipal User { get; } = new();

    public UserIdentifierType? UserId => null;

    public string? Role => null;

    public T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T> => null;
}

/// <summary>
/// An <see cref="ICurrentUserService"/> reporting a fixed identity, standing in for the HTTP
/// principal a scheduling request would carry.
/// </summary>
/// <param name="userId">The user id to report.</param>
/// <param name="roles">The roles to report.</param>
internal sealed class StubCurrentUserService(UserIdentifierType userId, string[] roles) : ICurrentUserService
{
    public ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
        [.. roles.Select(role => new Claim(ClaimTypes.Role, role))],
        "Test"));

    public UserIdentifierType? UserId => userId;

    public string? Role => roles.Length == 0 ? null : roles[0];

    public T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T> => null;
}

/// <summary>What the recording handler saw on one execution.</summary>
/// <param name="Value">The command payload it received.</param>
/// <param name="UserId">The user id the execution scope reported.</param>
/// <param name="Roles">The roles the execution scope reported.</param>
/// <param name="TenantId">The tenant the execution scope reported.</param>
internal sealed record RecordedExecution(string Value, UserIdentifierType? UserId, string[] Roles, string? TenantId);

/// <summary>Shared sink and outcome switch for <see cref="RecordingCommandHandler"/>.</summary>
internal sealed class ExecutionLog
{
    /// <summary>Gets every execution the handler saw, in order.</summary>
    public List<RecordedExecution> Executions { get; } = [];

    /// <summary>Gets or sets what the handler returns; defaults to success.</summary>
    public Func<Result> Outcome { get; set; } = Result.Success;

    /// <summary>Gets or sets an exception the handler throws instead of returning.</summary>
    public Func<Exception>? Throws { get; set; }
}

/// <summary>A command whose executions are recorded, used across the processor tests.</summary>
/// <param name="Value">Free-form payload; an empty value fails <see cref="RecordingCommandValidator"/>.</param>
internal sealed record RecordingCommand(string Value) : IInternalCommand;

/// <summary>Rejects an empty payload, so a test can prove the validating decorator ran.</summary>
internal sealed class RecordingCommandValidator : AbstractValidator<RecordingCommand>
{
    public RecordingCommandValidator() =>
        RuleFor(command => command.Value).NotEmpty().WithMessage("Value is required.");
}

/// <summary>Records what the execution scope reported, then returns the log's configured outcome.</summary>
internal sealed class RecordingCommandHandler(
    ExecutionLog log,
    ICurrentUserService currentUserService,
    ITenantContext tenantContext) : ICommandHandler<RecordingCommand, Result>
{
    public Task<Result> HandleAsync(RecordingCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        log.Executions.Add(new RecordedExecution(
            command.Value,
            currentUserService.UserId,
            [.. currentUserService.Roles],
            tenantContext.TenantId));

        if (log.Throws is { } factory)
        {
            throw factory();
        }

        return Task.FromResult(log.Outcome());
    }
}
