using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Webhooks;
using MMCA.Common.Shared.Abstractions;
using Testcontainers.MsSql;

namespace MMCA.Common.Infrastructure.SQLServer.Tests.Webhooks;

/// <summary>
/// The webhook delivery lane against a REAL SQL Server (design-webhook-lane.md 1(c), CI tier; rules
/// L1.3, L1.4, L1.13, L1.22). The host is the framework's own composition (<c>AddInfrastructure</c>
/// and <c>AddWebhooks</c> with <c>InternalCommands:Enabled</c> and <c>Webhooks:Enabled</c> true), so
/// the shared loop learns its exclusion from DI and the lane comes from its registered factory; both
/// loops are driven one cycle at a time through <c>ProcessDueCommandsAsync</c>. The delivery handler is
/// a recording stand-in, so no request leaves the test.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>sqlserver-integration</c>).
/// </para>
/// </summary>
public sealed class WebhookDeliveryLaneSqlServerTests : IAsyncLifetime
{
    // Same pinned 2022 CU tag as SQLServerPersistenceTests and WebhookSubscriptionSqlServerTests.
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04").Build();
    private readonly LaneProbeLog _log = new();

    private string _connectionString = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _sqlServer.StartAsync(Ct);

        await using (var master = new SqlConnection(_sqlServer.GetConnectionString()))
        {
            await master.OpenAsync(Ct);
            await using var create = master.CreateCommand();
            create.CommandText = "CREATE DATABASE [MmcaLaneTier]";
            await create.ExecuteNonQueryAsync(Ct);
        }

        _connectionString = new SqlConnectionStringBuilder(_sqlServer.GetConnectionString())
        {
            InitialCatalog = "MmcaLaneTier",
        }.ConnectionString;

        await using var host = BuildHost();
        await using var scope = host.Provider.CreateAsyncScope();
        await GetContext(scope.ServiceProvider).Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _sqlServer.DisposeAsync();

    // L1.3, L1.4: the shared loop runs the shared rows and leaves the delivery rows unclaimed; the lane
    // runs the delivery rows and never a shared one.
    [Fact]
    public async Task SharedLoopAndLane_PartitionTheQueueByCommandType()
    {
        await using var host = BuildHost();
        var first = NewDelivery();
        var second = NewDelivery();
        await SeedAsync(host, new SharedProbeCommand("a"), first, new SharedProbeCommand("b"), second);
        using var shared = ActivatorUtilities.CreateInstance<InternalCommandProcessor>(host.Provider);
        using var lane = ResolveLane(host);

        await shared.ProcessDueCommandsAsync(Ct);

        _log.Shared.Should().BeEquivalentTo("a", "b");
        _log.Deliveries.Should().BeEmpty("delivery rows belong to the lane");
        var afterShared = await LoadRowsAsync(host);
        afterShared.Where(r => r.CommandType == WebhookDeliveryLane.CommandType)
            .Should().AllSatisfy(r =>
            {
                r.ClaimedBy.Should().BeNull("the shared loop does not even claim a delivery row");
                r.Attempts.Should().Be(0);
            });

        await lane.ProcessDueCommandsAsync(Ct);

        _log.Deliveries.Should().BeEquivalentTo(new[] { first.DeliveryId, second.DeliveryId });
        _log.Shared.Should().HaveCount(2, "the lane never runs a shared row");
        (await LoadRowsAsync(host)).Should().AllSatisfy(r => r.ProcessedOn.Should().NotBeNull());
    }

    // L1.13: two replicas' lanes racing for one batch send every row exactly once, because the claim
    // and every renew and stamp are guarded by the claim token.
    [Fact]
    public async Task TwoLaneReplicas_RacingOneBatch_SendEachRowOnce()
    {
        await using var replicaA = BuildHost();
        await using var replicaB = BuildHost();
        var deliveries = Enumerable.Range(0, 20).Select(_ => NewDelivery()).ToArray();
        await SeedAsync(replicaA, [.. deliveries]);
        using var laneA = ResolveLane(replicaA);
        using var laneB = ResolveLane(replicaB);

        for (var round = 0; round < 5 && _log.Deliveries.Count < deliveries.Length; round++)
        {
            await Task.WhenAll(laneA.ProcessDueCommandsAsync(Ct), laneB.ProcessDueCommandsAsync(Ct));
        }

        _log.Deliveries.Should().HaveCount(deliveries.Length, "every row is sent once and only once");
        _log.Deliveries.Should().OnlyHaveUniqueItems();
        _log.Deliveries.Should().BeEquivalentTo(deliveries.Select(d => d.DeliveryId));
        (await LoadRowsAsync(replicaA)).Should().AllSatisfy(r => r.ProcessedOn.Should().NotBeNull());
    }

    // L1.22: while the lane is blocked inside a delivery, a shared-queue command due meanwhile runs at
    // once in the shared loop: a delivery attempt never delays a shared command on a replica with the lane.
    [Fact]
    public async Task ABlockedDelivery_DoesNotDelayASharedQueueCommand()
    {
        await using var host = BuildHost();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _log.DeliveryGate = async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var blocked = NewDelivery();
        await SeedAsync(host, blocked);
        using var shared = ActivatorUtilities.CreateInstance<InternalCommandProcessor>(host.Provider);
        using var lane = ResolveLane(host);

        var laneCycle = Task.Run(() => lane.ProcessDueCommandsAsync(Ct), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, Ct);

        await SeedAsync(host, new SharedProbeCommand("urgent"));
        await shared.ProcessDueCommandsAsync(Ct);

        _log.Shared.Should().Equal(["urgent"], "the shared loop never waits for a delivery");
        laneCycle.IsCompleted.Should().BeFalse("the delivery is still blocked");
        var urgent = (await LoadRowsAsync(host)).Single(r => r.CommandType != WebhookDeliveryLane.CommandType);
        urgent.ProcessedOn.Should().NotBeNull();

        release.TrySetResult();
        await laneCycle.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, Ct);
        _log.Deliveries.Should().Equal(blocked.DeliveryId);
    }

    private static DeliverWebhookInternalCommand NewDelivery() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "probe.happened", Guid.NewGuid(), DateTime.UtcNow, "{}");

    private static ApplicationDbContext GetContext(IServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory>().GetDbContext(
            services.GetRequiredService<IDataSourceResolver>().ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName));

    private static async Task SeedAsync(LaneTierHost host, params IInternalCommand[] commands)
    {
        await using var scope = host.Provider.CreateAsyncScope();
        var context = GetContext(scope.ServiceProvider);
        var due = DateTime.UtcNow.AddMinutes(-1);
        foreach (var command in commands)
        {
            context.Add(InternalCommandMessage.FromCommand(command, due, due, default));
        }

        await context.SaveChangesAsync(Ct);
    }

    private static async Task<List<InternalCommandMessage>> LoadRowsAsync(LaneTierHost host)
    {
        await using var scope = host.Provider.CreateAsyncScope();
        return await GetContext(scope.ServiceProvider).Set<InternalCommandMessage>().AsNoTracking().ToListAsync(Ct);
    }

    /// <summary>The lane exactly as the host would start it: built by its registered factory (1(d)).</summary>
    private static InternalCommandLaneProcessor ResolveLane(LaneTierHost host)
    {
        InternalCommandLaneProcessor? found = null;
        foreach (var descriptor in host.Services.Where(d =>
            !d.IsKeyedService && d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null))
        {
            var instance = descriptor.ImplementationFactory!(host.Provider);
            if (instance is InternalCommandLaneProcessor lane)
            {
                found.Should().BeNull("AddWebhooks registers one lane");
                found = lane;
            }
            else if (instance is IDisposable other)
            {
                other.Dispose();
            }
        }

        return found ?? throw new InvalidOperationException("AddWebhooks registered no delivery lane.");
    }

    private LaneTierHost BuildHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:SQLServerConnectionString"] = _connectionString,
                ["Webhooks:Enabled"] = "true",
                ["InternalCommands:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDomainEventDispatcher, NoOpDomainEventDispatcher>();
        services.AddInfrastructure(configuration);
        services.AddWebhooks(configuration);
        services.AddSingleton(_log);
        services.AddScoped<ICommandHandler<SharedProbeCommand, Result>, SharedProbeHandler>();
        services.AddScoped<ICommandHandler<DeliverWebhookInternalCommand, Result>, DeliveryProbeHandler>();
        return new LaneTierHost(services, services.BuildServiceProvider());
    }

    /// <summary>A shared-queue command, standing in for Store's paid-order deadline.</summary>
    /// <param name="Value">Identifies the row.</param>
    public sealed record SharedProbeCommand(string Value) : IInternalCommand;

    private sealed record LaneTierHost(ServiceCollection Services, ServiceProvider Provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class LaneProbeLog
    {
        public ConcurrentQueue<string> Shared { get; } = new();

        public ConcurrentQueue<Guid> Deliveries { get; } = new();

        public Func<CancellationToken, Task>? DeliveryGate { get; set; }
    }

    private sealed class SharedProbeHandler(LaneProbeLog log) : ICommandHandler<SharedProbeCommand, Result>
    {
        public Task<Result> HandleAsync(SharedProbeCommand command, CancellationToken cancellationToken = default)
        {
            log.Shared.Enqueue(command.Value);
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Stands in for the delivery handler: records the delivery id, never sends a request.</summary>
    private sealed class DeliveryProbeHandler(LaneProbeLog log) : ICommandHandler<DeliverWebhookInternalCommand, Result>
    {
        public async Task<Result> HandleAsync(DeliverWebhookInternalCommand command, CancellationToken cancellationToken = default)
        {
            if (log.DeliveryGate is { } gate)
            {
                await gate(cancellationToken).ConfigureAwait(false);
            }

            log.Deliveries.Enqueue(command.DeliveryId);
            return Result.Success();
        }
    }

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
