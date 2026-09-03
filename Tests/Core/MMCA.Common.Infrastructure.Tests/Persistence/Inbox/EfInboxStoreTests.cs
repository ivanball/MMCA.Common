using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Inbox;

/// <summary>
/// Proves the consumer-side idempotency inbox (#6) actually deduplicates against a real database with
/// the production unique constraint: a message id is recorded once, and a concurrent duplicate delivery
/// is absorbed by the unique index instead of thrown. This is what converts "consumers should be
/// idempotent" from convention into verified infrastructure (ADR-003 at-least-once-with-dedup).
/// </summary>
public sealed class EfInboxStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _contextServices;
    private readonly InboxTestDbContext _contextA;
    private readonly InboxTestDbContext _contextB;

    public EfInboxStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // The base ApplicationDbContext resolves its interceptors from a service provider during
        // OnConfiguring — mirror the OutboxProcessorTests fixture so the test context can be built.
        var contextServices = new ServiceCollection();
        contextServices.AddSingleton(TimeProvider.System);
        var dispatcher = Mock.Of<IDomainEventDispatcher>();
        contextServices.AddSingleton(dispatcher);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        var outboxSignal = Mock.Of<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            dispatcher, NullLogger<DomainEventSaveChangesInterceptor>.Instance, outboxSignal));
        contextServices.AddSingleton(Mock.Of<IEntityConfigurationAssemblyProvider>(
            p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        _contextServices = contextServices.BuildServiceProvider();

        // Two contexts over the SAME in-memory database simulate two consumer scopes racing to record
        // the same redelivered message.
        _contextA = CreateContext();
        _contextB = CreateContext();
        _contextA.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contextA.Dispose();
        _contextB.Dispose();
        _contextServices.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task MarkProcessed_ThenAlreadyProcessed_TrueForSameMessageOnly()
    {
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeFalse(
            "the message has not been processed yet");

        await store.MarkProcessedAsync(messageId, "TestIntegrationEvent", CancellationToken.None);

        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeTrue(
            "the message was just recorded as processed");
        (await store.AlreadyProcessedAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse(
            "an unrelated message id was never processed");
    }

    [Fact]
    public async Task ConcurrentDuplicateDelivery_AbsorbedByUniqueIndex_NoThrow_OneRow()
    {
        // Two consumer scopes both record the SAME message id — an at-least-once broker redelivering
        // before the first ack lands.
        var storeA = CreateStore(_contextA);
        var storeB = CreateStore(_contextB);
        var messageId = Guid.NewGuid();

        await storeA.MarkProcessedAsync(messageId, "UserRegistered", CancellationToken.None);

        // The unique index on MessageId rejects the duplicate insert; EfInboxStore must swallow the
        // DbUpdateException and treat the message as already-processed (idempotent), never rethrow.
        var duplicate = async () =>
            await storeB.MarkProcessedAsync(messageId, "UserRegistered", CancellationToken.None);
        await duplicate.Should().NotThrowAsync();

        var rows = await _contextA.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId);
        rows.Should().Be(1, "the unique index guarantees exactly one inbox row per message id");
    }

    [Fact]
    public async Task AfterAbsorbedDuplicate_TheSameScopeCanStillRecordAnotherMessage()
    {
        // The context is cached per data source for the whole scope, so the rejected row has to be
        // detached. Left Added, it poisoned the scope: every later save re-attempted the duplicate
        // insert and threw, taking an unrelated message down with it.
        var storeA = CreateStore(_contextA);
        var storeB = CreateStore(_contextB);
        var duplicated = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        await storeA.MarkProcessedAsync(duplicated, "UserRegistered", CancellationToken.None);
        await storeB.MarkProcessedAsync(duplicated, "UserRegistered", CancellationToken.None);

        // Same store, same (poisoned) context, a different message id.
        var next = async () => await storeB.MarkProcessedAsync(unrelated, "OrderPlaced", CancellationToken.None);
        await next.Should().NotThrowAsync("the rejected duplicate must not poison the scope's context");

        (await storeA.AlreadyProcessedAsync(duplicated, CancellationToken.None)).Should().BeTrue();
        (await storeA.AlreadyProcessedAsync(unrelated, CancellationToken.None)).Should().BeTrue(
            "the unrelated message must actually have been persisted");
        (await _contextA.Set<InboxMessage>().CountAsync(m => m.MessageId == duplicated)).Should().Be(1);
    }

    [Fact]
    public async Task NonDuplicateWriteFailure_IsRethrown_AndNothingIsRecorded()
    {
        // A write that fails for a reason OTHER than the unique index (here the NOT NULL EventType
        // column) must surface: swallowing it would ACK a message whose inbox row was never written,
        // so the broker never redelivers and the message is lost.
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        var failing = async () => await store.MarkProcessedAsync(messageId, null!, CancellationToken.None);
        await failing.Should().ThrowAsync<DbUpdateException>();

        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeFalse(
            "the row was never written, so the message must stay eligible for redelivery");
    }

    // ── Staging: the row rides the handler's own unit of work ──
    [Fact]
    public async Task TryBegin_StagesTheRowUnsaved_AndTheHandlersOwnSaveCommitsIt()
    {
        // This is the whole point of staging: the inbox row and the handler's mutations are written
        // by ONE SaveChangesAsync, so there is no instant where the handler's work is committed and
        // the message is not yet recorded. A crash in that instant used to reprocess the event.
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        (await store.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None)).Should().BeTrue(
            "an unseen message must be processed");

        // Nothing is in the database yet: a second scope still sees the message as unprocessed.
        (await _contextB.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(
            0, "TryBegin only STAGES the row; writing it is the handler's save");

        // The handler's save. It carries the staged inbox row with it.
        await _contextA.SaveChangesAsync(CancellationToken.None);

        (await _contextB.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(
            1, "the handler's own transaction committed the inbox row alongside its mutations");
    }

    [Fact]
    public async Task Complete_AfterTheHandlerAlreadySavedTheRow_WritesNothingFurther()
    {
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        await store.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None);
        await _contextA.SaveChangesAsync(CancellationToken.None);

        var complete = async () => await store.CompleteAsync(messageId, "OrderPlaced", CancellationToken.None);
        await complete.Should().NotThrowAsync("the row is already committed; completing must be a no-op");

        (await _contextA.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(
            1, "a second row would violate the unique index and prove the store double-wrote");
    }

    [Fact]
    public async Task Complete_WhenNoHandlerSaved_PersistsTheStagedRowItself()
    {
        // An event whose handlers write nothing (a cache warm, a push notification) never issues a
        // save, so the consume must still close out the inbox itself.
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        await store.TryBeginAsync(messageId, "CacheWarmed", CancellationToken.None);
        await store.CompleteAsync(messageId, "CacheWarmed", CancellationToken.None);

        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeTrue();
        (await _contextB.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(1);
    }

    [Fact]
    public async Task TryBegin_ForAnAlreadyProcessedMessage_ReturnsFalseAndStagesNothing()
    {
        // The redelivery path: the previous consume committed the row (with or without a handler
        // save), so this delivery must run no handlers and leave the context clean.
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();
        await store.MarkProcessedAsync(messageId, "OrderPlaced", CancellationToken.None);

        (await store.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None)).Should().BeFalse();

        await _contextA.SaveChangesAsync(CancellationToken.None);
        (await _contextA.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(
            1, "a refused TryBegin must not leave a second insert queued on the scope's context");
    }

    [Fact]
    public async Task Abandon_BeforeAnySave_DetachesTheStagedRow_SoTheRedeliveryReprocesses()
    {
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        await store.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None);
        store.Abandon(messageId).Should().BeTrue("the row never reached the database, so it was discarded cleanly");

        // A later save on the same (scope-lifetime) context must not resurrect the abandoned row.
        await _contextA.SaveChangesAsync(CancellationToken.None);

        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeFalse(
            "an abandoned consume must leave the message eligible for redelivery");
    }

    [Fact]
    public async Task Abandon_AfterAHandlerCommittedTheRow_ReportsThatTheRedeliveryWillBeSkipped()
    {
        // The sharp edge of atomicity: once one handler's save has committed the inbox row, a LATER
        // handler's failure cannot un-commit it, and the redelivery will be deduplicated. The store
        // reports that honestly rather than pretending the message is still open.
        var store = CreateStore(_contextA);
        var messageId = Guid.NewGuid();

        await store.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None);
        await _contextA.SaveChangesAsync(CancellationToken.None);

        store.Abandon(messageId).Should().BeFalse();
        (await store.AlreadyProcessedAsync(messageId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentDelivery_StagedRowLosesToTheUniqueIndex_AndIsAbsorbedOnComplete()
    {
        // Two scopes consume the same redelivered message and both stage a row. The second save is
        // rejected by the unique index on MessageId, which the store treats as already-processed.
        var storeA = CreateStore(_contextA);
        var storeB = CreateStore(_contextB);
        var messageId = Guid.NewGuid();

        await storeA.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None);
        await storeB.TryBeginAsync(messageId, "OrderPlaced", CancellationToken.None);

        await storeA.CompleteAsync(messageId, "OrderPlaced", CancellationToken.None);

        var duplicate = async () => await storeB.CompleteAsync(messageId, "OrderPlaced", CancellationToken.None);
        await duplicate.Should().NotThrowAsync("a concurrent duplicate delivery is idempotent, not an error");

        (await _contextA.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId)).Should().Be(1);
    }

    private static EfInboxStore CreateStore(ApplicationDbContext context)
    {
        var factory = new Mock<IDbContextFactory>();
        factory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(DataSourceKey.Default(DataSource.Sqlite));

        return new EfInboxStore(
            factory.Object,
            resolver.Object,
            Options.Create(new OutboxSettings()),
            NullLogger<EfInboxStore>.Instance);
    }

    private InboxTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InboxTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new InboxTestDbContext(
            options,
            _contextServices,
            Mock.Of<IEntityConfigurationAssemblyProvider>(
                p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));
    }

    /// <summary>
    /// A test <see cref="ApplicationDbContext"/> that maps only <see cref="InboxMessage"/> (with the
    /// production unique index on <see cref="InboxMessage.MessageId"/>) so the model is SQLite-portable.
    /// </summary>
    private sealed class InboxTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<InboxMessage>(entity =>
            {
                entity.ToTable("InboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.HasIndex(e => e.MessageId).IsUnique();
            });
    }
}
