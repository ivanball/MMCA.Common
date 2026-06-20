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
using MMCA.Common.Infrastructure.Settings;
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
