using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.Webhooks;
using MMCA.Common.Infrastructure.Webhooks;
using MMCA.Common.Shared.Abstractions;
using Testcontainers.MsSql;

namespace MMCA.Common.Infrastructure.SQLServer.Tests.Webhooks;

/// <summary>
/// The webhook table and the fan-out against a REAL SQL Server (design 35(c) "CI only", rules 35.7,
/// 35.25 and 35.27). The unit tier builds the model and runs the store on SQLite; only a server can
/// say that <c>EnsureCreated</c> emits the <c>(TenantId, IsDeleted)</c> index and the varchar columns,
/// and that the delivery rows one event fans out to commit together or not at all. The host is the
/// framework's own composition (<c>AddInfrastructure</c>, <c>AddWebhooks</c>,
/// <c>AddWebhookEvent</c>); only the in-process domain-event dispatcher is a no-op.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>sqlserver-integration</c>).
/// </para>
/// </summary>
public sealed class WebhookSubscriptionSqlServerTests : IAsyncLifetime
{
    private const string EventType = "probe.happened";
    private const string DeliverCommandName = "MMCA.Webhooks.Deliver.v1";
    private const string QualifiedTable = "dbo.WebhookSubscriptions";

    // Same pinned 2022 CU tag as SQLServerPersistenceTests, so the two move together.
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04").Build();

    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _sqlServer.StartAsync();

        await using (var master = new SqlConnection(_sqlServer.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = "CREATE DATABASE [MmcaWebhookTier]";
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new SqlConnectionStringBuilder(_sqlServer.GetConnectionString())
        {
            InitialCatalog = "MmcaWebhookTier",
        }.ConnectionString;

        await using var provider = BuildHost();
        await using var scope = provider.CreateAsyncScope();
        await GetContext(scope.ServiceProvider).Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _sqlServer.DisposeAsync();

    [Fact]
    public async Task TableCreatedWithIndex()
    {
        await using var provider = BuildHost();
        await using var scope = provider.CreateAsyncScope();
        var context = GetContext(scope.ServiceProvider);
        var ct = TestContext.Current.CancellationToken;

        var indexColumns = await context.Database
            .SqlQuery<string>($"""
                SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS [Value]
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID({QualifiedTable}) AND i.name = {WebhookSubscriptionModelBuilderExtensions.TenantIndexName}
                  AND ic.is_included_column = 0
                """)
            .SingleAsync(ct);

        indexColumns.Should().Be("TenantId,IsDeleted", "every store read filters by tenant and the soft-delete flag");
        (await ColumnTypeAsync(context, "Url", ct)).Should().Be("nvarchar(2048)");
        (await ColumnTypeAsync(context, "EventTypes", ct)).Should().Be("varchar(2048)");
        (await ColumnTypeAsync(context, "TenantId", ct)).Should().Be("varchar(64)");
        (await ColumnTypeAsync(context, "ProtectedSecret", ct)).Should().Be("nvarchar(1024)");
        (await ColumnTypeAsync(context, "LastFailure", ct)).Should().Be("nvarchar(500)");
        (await ColumnTypeAsync(context, "RowVersion", ct)).Should().Be("timestamp", "the auditable row version is a server rowversion");
    }

    [Fact]
    public async Task FanOutRowsCommitAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSubscriptionsAsync(count: 3, ct);

        // A scheduler that fails the SECOND of the three schedules: the first row is already written
        // on the transaction, so only a real rollback leaves the table without it.
        var failing = new ProbeHappened("fails");
        await using (var provider = BuildHost(services => services.AddScoped<IInternalCommandScheduler>(sp =>
            new FailSecondScheduleScheduler(ActivatorUtilities.CreateInstance<InternalCommandScheduler>(sp)))))
        {
            var act = () => FanOutAsync(provider, failing, ct);

            await act.Should().ThrowAsync<Exception>("a failed schedule fails the fan-out so the event is redelivered (rule 35.27)");
        }

        (await CountDeliveryRowsAsync(failing.MessageId, ct)).Should().Be(0, "the rows of one fan-out commit together or not at all");

        var succeeding = new ProbeHappened("succeeds");
        await using (var provider = BuildHost())
        {
            await FanOutAsync(provider, succeeding, ct);
        }

        (await CountDeliveryRowsAsync(succeeding.MessageId, ct)).Should().Be(3, "one delivery row per matching subscription");
    }

    private static ApplicationDbContext GetContext(IServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory>().GetDbContext(
            services.GetRequiredService<IDataSourceResolver>().ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName));

    private static Task<string> ColumnTypeAsync(ApplicationDbContext context, string column, CancellationToken ct) =>
        context.Database
            .SqlQuery<string>($"""
                SELECT DATA_TYPE + CASE WHEN CHARACTER_MAXIMUM_LENGTH IS NULL THEN '' ELSE '(' + CAST(CHARACTER_MAXIMUM_LENGTH AS nvarchar(10)) + ')' END AS [Value]
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = {WebhookSubscriptionModelBuilderExtensions.TableName} AND COLUMN_NAME = {column}
                """)
            .SingleAsync(ct);

    private static async Task FanOutAsync(ServiceProvider provider, ProbeHappened integrationEvent, CancellationToken ct)
    {
        var handler = provider.GetServices<IIntegrationEventHandler<ProbeHappened>>().Should().ContainSingle(
            "AddWebhookEvent registers the fan-out as the event's integration handler").Which;
        await handler.HandleAsync(integrationEvent, ct);
    }

    private async Task SeedSubscriptionsAsync(int count, CancellationToken ct)
    {
        await using var provider = BuildHost();
        for (var index = 0; index < count; index++)
        {
            await using var scope = provider.CreateAsyncScope();
            var subscription = WebhookSubscription.Create(
                $"https://hooks{index}.example.com/in", null, [EventType], tenantId: null, "protected-secret").Value!;
            await scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionStore>().AddAsync(subscription, ct);
        }
    }

    private async Task<int> CountDeliveryRowsAsync(Guid eventId, CancellationToken ct)
    {
        await using var provider = BuildHost();
        await using var scope = provider.CreateAsyncScope();
        var marker = eventId.ToString("D");
        return await GetContext(scope.ServiceProvider).Set<InternalCommandMessage>()
            .CountAsync(m => m.CommandType == DeliverCommandName && m.Payload.Contains(marker), ct);
    }

    private ServiceProvider BuildHost(Action<IServiceCollection>? adjust = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:SQLServerConnectionString"] = _connectionString,
                ["Webhooks:Enabled"] = "true",

                // Write rows only: no processor would run here anyway, since no host is started.
                ["InternalCommands:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDomainEventDispatcher, NoOpDomainEventDispatcher>();
        services.AddInfrastructure(configuration);
        services.AddWebhooks(configuration);
        services.AddWebhookEvent<ProbeHappened>(EventType);
        adjust?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public sealed record ProbeHappened(string Code) : BaseDomainEvent, IIntegrationEvent;

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Delegates to the real scheduler except on the second call, which fails as a Result.</summary>
    private sealed class FailSecondScheduleScheduler(IInternalCommandScheduler inner) : IInternalCommandScheduler
    {
        private int _calls;

        public Task<Result<Guid>> ScheduleAsync(IInternalCommand command, DateTimeOffset? runAt = null, CancellationToken cancellationToken = default) =>
            Next(() => inner.ScheduleAsync(command, runAt, cancellationToken));

        public Task<Result<Guid>> ScheduleAsync(IInternalCommand command, TimeSpan delay, CancellationToken cancellationToken = default) =>
            Next(() => inner.ScheduleAsync(command, delay, cancellationToken));

        private Task<Result<Guid>> Next(Func<Task<Result<Guid>>> schedule) =>
            Interlocked.Increment(ref _calls) == 2
                ? Task.FromResult(Result.Failure<Guid>(Error.Failure("Test.ScheduleFailed", "The second schedule fails.")))
                : schedule();
    }
}
