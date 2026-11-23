using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Administration;

/// <summary>
/// <see cref="OutboxAdministration"/>: the operator path that gives a dead-lettered event a way back
/// into delivery, instead of leaving "wait for the retention sweep to delete it" as the only ending.
/// Exercised against a real in-memory SQLite outbox table so the set-based replay update and the
/// dead-letter predicate are the ones that would run in production.
/// </summary>
public sealed class OutboxAdministrationTests : IDisposable
{
    private const int MaxRetries = 3;

    private static readonly DateTime Origin = new(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly AdminTestContext _context;
    private readonly ServiceProvider _scopeServices;
    private readonly Mock<IOutboxSignal> _signal = new();
    private readonly FrameworkTableTargets _tableTargets;
    private readonly OutboxAdministration _sut;

    public OutboxAdministrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = AdminTestContext.Create(_connection);

        var contextFactory = new Mock<IDbContextFactory>();
        contextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(_context);

        var services = new ServiceCollection();
        services.AddScoped(_ => contextFactory.Object);
        _scopeServices = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        _tableTargets = new FrameworkTableTargets(registry.Object, resolver.Object);

        // A broker host with the inbox on (the broker default): the posture a republish is meant for.
        _sut = CreateSut(new MessageBusSettings { Provider = MessageBusProviders.RabbitMq });
    }

    public void Dispose()
    {
        _context.Dispose();
        _scopeServices.Dispose();
        _connection.Dispose();
    }

    // ── Listing: dead letters only, oldest first, payload deliberately not projected ──
    [Fact]
    public async Task ListDeadLetters_ReturnsOnlyExhaustedUnprocessedRows_OldestFirst()
    {
        OutboxMessage older = DeadLetter(Origin.AddHours(-5), "shipment-1");
        OutboxMessage newer = DeadLetter(Origin.AddHours(-1), orderingKey: null);
        OutboxMessage stillRetrying = Pending(Origin.AddHours(-4), retryCount: 1);
        OutboxMessage processed = Pending(Origin.AddHours(-6), retryCount: MaxRetries);
        processed.ProcessedOn = Origin;
        await SeedAsync(older, newer, stillRetrying, processed);

        var result = await _sut.ListDeadLettersAsync(null, skip: 0, take: 50, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(d => d.Id).Should().Equal(
            [older.Id, newer.Id],
            "a row is a dead letter only when it is unprocessed AND out of retries, and the list is chronological");
        OutboxDeadLetter oldest = result.Value![0];
        oldest.LastError.Should().Be("boom", "the failure is the first thing an operator needs");
        oldest.OrderingKey.Should().Be("shipment-1");
        oldest.DataSource.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListDeadLetters_AppliesPaging()
    {
        OutboxMessage first = DeadLetter(Origin.AddHours(-3), orderingKey: null);
        OutboxMessage second = DeadLetter(Origin.AddHours(-2), orderingKey: null);
        OutboxMessage third = DeadLetter(Origin.AddHours(-1), orderingKey: null);
        await SeedAsync(first, second, third);

        var page = await _sut.ListDeadLettersAsync(null, skip: 1, take: 1, CancellationToken.None);

        page.Value!.Select(d => d.Id).Should().Equal(second.Id);
    }

    [Fact]
    public async Task ListDeadLetters_RejectsAnUnusableWindow()
    {
        (await _sut.ListDeadLettersAsync(null, skip: -1, take: 10, CancellationToken.None))
            .IsFailure.Should().BeTrue();
        (await _sut.ListDeadLettersAsync(null, skip: 0, take: 0, CancellationToken.None))
            .IsFailure.Should().BeTrue();
        (await _sut.ListDeadLettersAsync(null, skip: 0, take: 5_000, CancellationToken.None))
            .IsFailure.Should().BeTrue("an admin call must not be able to ask for the whole table");
    }

    [Fact]
    public async Task ListDeadLetters_UnknownDataSource_FailsInsteadOfReturningNothing()
    {
        await SeedAsync(DeadLetter(Origin.AddHours(-1), orderingKey: null));

        var result = await _sut.ListDeadLettersAsync("Nope/Missing", skip: 0, take: 10, CancellationToken.None);

        result.IsFailure.Should().BeTrue(
            "an empty page and a mistyped source name must not look the same to an operator");
        result.Errors[0].Code.Should().Be("Outbox.UnknownDataSource");
    }

    // ── Replay: retries reset, claim cleared, error history kept, processor woken ──
    [Fact]
    public async Task ReplayDeadLetters_ResetsRetriesAndClearsTheClaim_ButKeepsTheError()
    {
        OutboxMessage dead = DeadLetter(Origin.AddHours(-2), orderingKey: null);
        dead.LockedUntil = Origin.AddMinutes(30);
        dead.LockToken = Guid.NewGuid();
        await SeedAsync(dead);

        var replayed = await _sut.ReplayDeadLettersAsync(null, null, CancellationToken.None);

        replayed.IsSuccess.Should().BeTrue();
        replayed.Value.Should().Be(1);

        OutboxMessage row = await ReloadAsync(dead.Id);
        row.RetryCount.Should().Be(0, "a zero retry count is what returns the row to the poll's predicate");
        row.LockedUntil.Should().BeNull("the row must be claimable on the very next cycle, not after the lease");
        row.LockToken.Should().BeNull();
        row.ProcessedOn.Should().BeNull();
        row.OccurredOn.Should().Be(dead.OccurredOn, "a replay must not move the row within its ordering key");
        row.LastError.Should().Be("boom", "the reason it failed is the history a replay is judged against");
        _signal.Verify(s => s.Signal(), Times.Once, "waiting out a 300s poll interval after a replay is not acceptable");
    }

    [Fact]
    public async Task ReplayDeadLetters_WithExplicitIds_TouchesOnlyThoseRows()
    {
        OutboxMessage chosen = DeadLetter(Origin.AddHours(-2), orderingKey: null);
        OutboxMessage other = DeadLetter(Origin.AddHours(-1), orderingKey: null);
        await SeedAsync(chosen, other);

        var replayed = await _sut.ReplayDeadLettersAsync(null, [chosen.Id], CancellationToken.None);

        replayed.Value.Should().Be(1);
        (await ReloadAsync(chosen.Id)).RetryCount.Should().Be(0);
        (await ReloadAsync(other.Id)).RetryCount.Should().Be(MaxRetries, "the unnamed row is left alone");
    }

    [Fact]
    public async Task ReplayDeadLetters_LeavesStillRetryingRowsAlone()
    {
        OutboxMessage stillRetrying = Pending(Origin.AddHours(-2), retryCount: 1);
        await SeedAsync(stillRetrying);

        var replayed = await _sut.ReplayDeadLettersAsync(null, null, CancellationToken.None);

        replayed.Value.Should().Be(0);
        (await ReloadAsync(stillRetrying.Id)).RetryCount.Should().Be(
            1,
            "replaying a row that has not given up yet would reset a backoff that is doing its job");
        _signal.Verify(s => s.Signal(), Times.Never);
    }

    // ── Counting: what is still deliverable, right now ──
    [Fact]
    public async Task CountPending_CountsUndeliveredRowsThatStillHaveRetriesLeft()
    {
        OutboxMessage pending = Pending(Origin.AddHours(-1), retryCount: 0);
        OutboxMessage retrying = Pending(Origin.AddHours(-2), retryCount: 2);
        OutboxMessage dead = DeadLetter(Origin.AddHours(-3), orderingKey: null);
        OutboxMessage processed = Pending(Origin.AddHours(-4), retryCount: 0);
        processed.ProcessedOn = Origin;
        await SeedAsync(pending, retrying, dead, processed);

        var count = await _sut.CountPendingAsync(null, CancellationToken.None);

        count.IsSuccess.Should().BeTrue();
        count.Value.Should().Be(2, "dead letters are not pending work, and processed rows are done");
    }

    // L180: skip + take in int arithmetic wraps negative near int.MaxValue; SQLite would read the
    // wrapped LIMIT as "no limit", while SQL Server and PostgreSQL reject it and fail the call.
    [Fact]
    public async Task ListDeadLetters_WithASkipNearIntMax_SendsNoNegativeLimitAndReturnsAnEmptyPage()
    {
        await SeedAsync(DeadLetter(Origin.AddHours(-1), orderingKey: null));

        var result = await _sut.ListDeadLettersAsync(null, skip: int.MaxValue - 5, take: 10, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty("a page past the end is empty, not an error");
        _context.NegativeParameters.Negatives.Should().BeEmpty(
            "a wrapped negative page size is rejected by SQL Server and PostgreSQL");
    }

    // -- Republish window: integration rows processed inside [from, to) go back to pending --
    [Fact]
    public async Task RepublishProcessed_ResetsOnlyIntegrationRowsProcessedInsideTheWindow()
    {
        var from = Origin;
        var to = Origin.AddHours(2);
        OutboxMessage atStart = Processed<RepublishIntegrationEvent>(from);
        OutboxMessage inside = Processed<RepublishIntegrationEvent>(from.AddMinutes(30));
        inside.RetryCount = 2;
        inside.LastError = "transient";
        inside.LockedUntil = from.AddMinutes(35);
        inside.LockToken = Guid.NewGuid();
        OutboxMessage atEnd = Processed<RepublishIntegrationEvent>(to);
        OutboxMessage before = Processed<RepublishIntegrationEvent>(from.AddTicks(-1));
        OutboxMessage domainInside = Processed<RepublishDomainEvent>(from.AddMinutes(10));
        OutboxMessage unresolvableInside = Processed<RepublishIntegrationEvent>(from.AddMinutes(20), eventType: "Gone.Event, Gone");
        OutboxMessage pending = Pending(from.AddMinutes(-5), retryCount: 1);
        await SeedAsync(atStart, inside, atEnd, before, domainInside, unresolvableInside, pending);

        var result = await _sut.RepublishProcessedAsync(from, to, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2, "the window is [from, to): the start is included and the end is not");

        OutboxMessage row = await ReloadAsync(inside.Id);
        row.ProcessedOn.Should().BeNull("a cleared stamp is what returns the row to the poll's predicate");
        row.RetryCount.Should().Be(0, "a republished row gets its full retry budget again");
        row.LockedUntil.Should().BeNull("the row must be claimable on the very next cycle");
        row.LockToken.Should().BeNull();
        row.OccurredOn.Should().Be(inside.OccurredOn, "a republish must not move the row within its ordering key");
        row.LastError.Should().Be("transient", "the row's history is kept, as on a dead-letter replay");
        (await ReloadAsync(atStart.Id)).ProcessedOn.Should().BeNull();

        (await ReloadAsync(atEnd.Id)).ProcessedOn.Should().Be(to, "the window end is exclusive");
        (await ReloadAsync(before.Id)).ProcessedOn.Should().Be(from.AddTicks(-1));
        (await ReloadAsync(domainInside.Id)).ProcessedOn.Should().NotBeNull(
            "a domain event's handlers run in-process with no inbox and already ran, so it is never republished");
        (await ReloadAsync(unresolvableInside.Id)).ProcessedOn.Should().NotBeNull(
            "a stored name this host cannot resolve could not be published again either");
        (await ReloadAsync(pending.Id)).RetryCount.Should().Be(1, "a row still pending is not the republish's business");
        _signal.Verify(s => s.Signal(), Times.Once, "the processor is woken rather than left to its polling interval");
    }

    [Fact]
    public async Task RepublishProcessed_WithNothingInTheWindow_ReturnsZeroAndDoesNotWakeTheProcessor()
    {
        await SeedAsync(Processed<RepublishIntegrationEvent>(Origin.AddDays(-1)));

        var result = await _sut.RepublishProcessedAsync(Origin, Origin.AddHours(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        _signal.Verify(s => s.Signal(), Times.Never);
    }

    [Fact]
    public async Task RepublishProcessed_AcceptsAWindowOfExactlySevenDays()
    {
        OutboxMessage inside = Processed<RepublishIntegrationEvent>(Origin.AddDays(6));
        await SeedAsync(inside);

        var result = await _sut.RepublishProcessedAsync(Origin, Origin.AddDays(7), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
    }

    [Theory]
    [InlineData(DateTimeKind.Local, DateTimeKind.Utc, 1, "Outbox.RepublishWindowNotUtc")]
    [InlineData(DateTimeKind.Utc, DateTimeKind.Unspecified, 1, "Outbox.RepublishWindowNotUtc")]
    [InlineData(DateTimeKind.Utc, DateTimeKind.Utc, 0, "Outbox.InvalidRepublishWindow")]
    [InlineData(DateTimeKind.Utc, DateTimeKind.Utc, -1, "Outbox.InvalidRepublishWindow")]
    [InlineData(DateTimeKind.Utc, DateTimeKind.Utc, 7 * 24 + 1, "Outbox.RepublishWindowTooWide")]
    public async Task RepublishProcessed_RejectsAnInvalidWindow_AndTouchesNothing(
        DateTimeKind fromKind,
        DateTimeKind toKind,
        int hours,
        string expectedCode)
    {
        OutboxMessage inside = Processed<RepublishIntegrationEvent>(Origin.AddMinutes(1));
        await SeedAsync(inside);
        var from = DateTime.SpecifyKind(Origin, fromKind);
        var to = DateTime.SpecifyKind(Origin.AddHours(hours), toKind);

        var result = await _sut.RepublishProcessedAsync(from, to, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be(expectedCode);
        (await ReloadAsync(inside.Id)).ProcessedOn.Should().NotBeNull();
        _signal.Verify(s => s.Signal(), Times.Never);
    }

    public static TheoryData<string, bool?> InboxOffPostures => new()
    {
        // The in-process bus default: no inbox, handlers run in-process.
        { MessageBusProviders.InProcess, null },
        // A broker host that opted out of the inbox explicitly.
        { MessageBusProviders.RabbitMq, false },
    };

    [Theory]
    [MemberData(nameof(InboxOffPostures))]
    public async Task RepublishProcessed_WithoutTheInbox_IsRefused_AndTouchesNothing(string provider, bool? enableInbox)
    {
        var sut = CreateSut(new MessageBusSettings { Provider = provider, EnableInbox = enableInbox });
        OutboxMessage inside = Processed<RepublishIntegrationEvent>(Origin.AddMinutes(1));
        await SeedAsync(inside);

        var result = await sut.RepublishProcessedAsync(Origin, Origin.AddHours(1), CancellationToken.None);

        result.IsFailure.Should().BeTrue("without the inbox a republished event would run its handlers again");
        result.Errors[0].Code.Should().Be("Outbox.RepublishRequiresInbox");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
        (await ReloadAsync(inside.Id)).ProcessedOn.Should().NotBeNull("a refused republish resets no row");
        _signal.Verify(s => s.Signal(), Times.Never);
    }

    [Fact]
    public async Task RepublishProcessed_WithTheInboxForcedOnUnderInProcess_Runs()
    {
        var sut = CreateSut(new MessageBusSettings { Provider = MessageBusProviders.InProcess, EnableInbox = true });
        OutboxMessage inside = Processed<RepublishIntegrationEvent>(Origin.AddMinutes(1));
        await SeedAsync(inside);

        var result = await sut.RepublishProcessedAsync(Origin, Origin.AddHours(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the guard keys on the resolved inbox posture, not on the provider name");
        result.Value.Should().Be(1);
    }

    // ── Helpers ──
    private OutboxAdministration CreateSut(MessageBusSettings messageBus) => new(
        _scopeServices.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<OutboxAdministration>.Instance,
        Options.Create(new OutboxSettings { MaxRetries = MaxRetries, DataSource = DataSource.Sqlite }),
        _tableTargets,
        _signal.Object,
        Options.Create(messageBus));

    private static OutboxMessage DeadLetter(DateTime occurredOn, string? orderingKey) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "Test.Event, Test",
        Payload = "{}",
        OccurredOn = occurredOn,
        RetryCount = MaxRetries,
        LastError = "boom",
        OrderingKey = orderingKey,
    };

    private static OutboxMessage Pending(DateTime occurredOn, int retryCount) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "Test.Event, Test",
        Payload = "{}",
        OccurredOn = occurredOn,
        RetryCount = retryCount,
    };

    private static OutboxMessage Processed<TEvent>(DateTime processedOn, string? eventType = null) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType ?? typeof(TEvent).AssemblyQualifiedName!,
        Payload = "{}",
        OccurredOn = processedOn.AddSeconds(-1),
        ProcessedOn = processedOn,
    };

    private async Task SeedAsync(params OutboxMessage[] messages)
    {
        _context.Set<OutboxMessage>().AddRange(messages);
        await _context.SaveChangesAsync(CancellationToken.None);
        _context.ChangeTracker.Clear();
    }

    private Task<OutboxMessage> ReloadAsync(Guid id) =>
        _context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, CancellationToken.None);

    /// <summary>An integration event: its processed rows are what a republish resets.</summary>
    private sealed record RepublishIntegrationEvent : BaseDomainEvent, IIntegrationEvent;

    /// <summary>A pure domain event: its processed rows are never reset.</summary>
    private sealed record RepublishDomainEvent : BaseDomainEvent;

    /// <summary>A test <see cref="ApplicationDbContext"/> mapping <see cref="OutboxMessage"/> only.</summary>
    private sealed class AdminTestContext : ApplicationDbContext
    {
        private AdminTestContext(DbContextOptions<AdminTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NoAssemblies(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static AdminTestContext Create(SqliteConnection connection)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>(), timeProvider: TimeProvider.System));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            ServiceProvider provider = services.BuildServiceProvider();

            var recorder = new NegativeParameterRecorder();
            var options = new DbContextOptionsBuilder<AdminTestContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options;

            var context = new AdminTestContext(options, provider) { NegativeParameters = recorder };
            context.Database.EnsureCreated();
            return context;
        }

        /// <summary>Gets the negative integer parameters this context's queries sent.</summary>
        public NegativeParameterRecorder NegativeParameters { get; private init; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(4000);
                entity.Property(e => e.OrderingKey).HasMaxLength(200);
            });

        private sealed class NoAssemblies : IEntityConfigurationAssemblyProvider
        {
            public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
        }
    }
}
