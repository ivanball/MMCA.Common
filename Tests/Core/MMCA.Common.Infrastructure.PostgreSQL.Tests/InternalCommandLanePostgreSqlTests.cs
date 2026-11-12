using System.Collections.Concurrent;
using AwesomeAssertions;
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
using Testcontainers.PostgreSql;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>
/// The webhook delivery lane's queue partition against a REAL PostgreSQL server
/// (design-webhook-lane.md 1(c), CI tier; rules L1.3, L1.4). The unit tier runs the shared loop's
/// guarded <c>CommandType</c> predicate and the lane's claim on SQLite; only this server can say that
/// Npgsql translates both, with the quoted identifiers and the parameterized string comparison, and
/// that the two loops partition one table between them. The host is the framework's own composition
/// (<c>AddInfrastructure</c> plus <c>AddWebhooks</c>, both flags on, the queue pointed at PostgreSQL);
/// the delivery handler is a recording stand-in.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>postgresql-integration</c>).
/// </para>
/// </summary>
public sealed class InternalCommandLanePostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly LaneProbeLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var host = BuildHost();
        await using var scope = host.Provider.CreateAsyncScope();
        await GetContext(scope.ServiceProvider).Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

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
        (await LoadRowsAsync(host)).Where(r => r.CommandType == WebhookDeliveryLane.CommandType)
            .Should().AllSatisfy(r =>
            {
                r.ClaimedBy.Should().BeNull("the shared loop does not even claim a delivery row");
                r.Attempts.Should().Be(0);
            });

        await lane.ProcessDueCommandsAsync(Ct);

        _log.Deliveries.Should().BeEquivalentTo(first.DeliveryId, second.DeliveryId);
        _log.Shared.Should().HaveCount(2, "the lane never runs a shared row");
        (await LoadRowsAsync(host)).Should().AllSatisfy(r => r.ProcessedOn.Should().NotBeNull());
    }

    private static DeliverWebhookInternalCommand NewDelivery() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "probe.happened", Guid.NewGuid(), DateTime.UtcNow, "{}");

    private static ApplicationDbContext GetContext(IServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory>().GetDbContext(
            services.GetRequiredService<IDataSourceResolver>().ResolveLogical(DataSource.PostgreSQL, DataSourceKey.DefaultName));

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
                ["ConnectionStrings:PostgreSQLConnectionString"] = _postgres.GetConnectionString(),
                ["InternalCommands:DataSource"] = nameof(DataSource.PostgreSQL),
                ["InternalCommands:Enabled"] = "true",
                ["Webhooks:Enabled"] = "true",
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

    /// <summary>A shared-queue command.</summary>
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
        public Task<Result> HandleAsync(DeliverWebhookInternalCommand command, CancellationToken cancellationToken = default)
        {
            log.Deliveries.Enqueue(command.DeliveryId);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
