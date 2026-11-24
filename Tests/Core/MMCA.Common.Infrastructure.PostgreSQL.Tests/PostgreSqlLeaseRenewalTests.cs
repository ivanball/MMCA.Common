using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using Npgsql;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>
/// Lease renewal of the PostgreSQL transport against a REAL PostgreSQL server. In production a claim
/// holds for 60 seconds and is renewed every 20, too slow to observe, so these tests run the receiver
/// with a two-second lease renewed every 400 milliseconds and a handler that takes three leases: without
/// renewal the row would become claimable again mid-processing and a competing receiver would get it.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>postgresql-integration</c>).
/// </para>
/// </summary>
public sealed class PostgreSqlLeaseRenewalTests(PostgreSqlBrokerFixture fixture) : IClassFixture<PostgreSqlBrokerFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HandlerTime = Lease * 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _schema = "lease_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _queue = "billing-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _topic = "order-placed-" + Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AHandlerSlowerThanTheLease_KeepsItsClaim_SoACompetingReceiverNeverGetsIt_AndItIsCompletedOnce()
    {
        var pipeline = new SlowPipeline(HandlerTime);
        await using var first = await StartHostAsync(pipeline);
        await using var second = await StartHostAsync(pipeline);
        await WaitUntilBoundAsync();

        await first.Publisher.PublishAsync(Message(), Ct);
        await pipeline.Entered.Task.WaitAsync(Timeout, Ct);

        // Past the first lease, while the handler still runs: the claim is still held, by the original
        // delivery, so neither receiver can take the row.
        await Task.Delay(Lease * 1.5, Ct);
        var (deliveryCount, held) = await ReadClaimAsync();
        deliveryCount.Should().Be(1, "the row is claimed once while it is being processed");
        held.Should().BeTrue("the receiver renews the lease of a row still in flight");

        await pipeline.Finished.Task.WaitAsync(Timeout, Ct);
        await WaitUntilSettledAsync();

        // Long enough for a lapsed lease to be claimed again by either receiver.
        await Task.Delay(Lease + PollInterval * 3, Ct);
        pipeline.Deliveries.Should().Be(1, "a competing receiver must not get a message whose claim is renewed");
        (await CountAsync("dead_letters")).Should().Be(0, "the message was completed, not dead-lettered");
    }

    [Fact]
    public async Task RenewAsync_ExtendsOnlyAClaimStillHeld_AndALostClaimCannotSettle()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var store = new PostgreSqlMessageStore(dataSource, ownsDataSource: false, new PostgreSqlTransportSql(_schema, PostgreSqlTransportSettings.DefaultName));
        await store.EnsureSchemaAsync(Ct);
        await store.SubscribeAsync(_queue, [_topic], Ct);
        await store.PublishAsync(Message(), Ct);

        var original = (await store.ClaimAsync(_queue, 1, TimeSpan.FromSeconds(1), Ct)).Should().ContainSingle().Subject;
        await store.RenewAsync([original], TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        (await store.ClaimAsync(_queue, 1, Lease, Ct)).Should().BeEmpty("the renewal outlived the one-second claim");

        // Lapse the original claim, and let another claimer take the row.
        await store.RenewAsync([original], TimeSpan.Zero, Ct);
        var takeover = (await store.ClaimAsync(_queue, 1, TimeSpan.FromSeconds(30), Ct)).Should().ContainSingle().Subject;
        takeover.DeliveryCount.Should().Be(2);

        // The original claimer's renewal no longer matches the row: a zero lease would free it if it did.
        await store.RenewAsync([original], TimeSpan.Zero, Ct);
        (await store.ClaimAsync(_queue, 1, Lease, Ct)).Should().BeEmpty("a renewal under a lost claim must not touch the new one");

        (await store.CompleteAsync(original, Ct)).Should().BeFalse("the original claim was lost");
        (await store.CompleteAsync(takeover, Ct)).Should().BeTrue();
        (await CountAsync("messages")).Should().Be(0);
    }

    private static OutgoingBrokerMessage Message(string topic) =>
        new(
            topic,
            "order-placed",
            Guid.NewGuid(),
            "{}"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal) { [BrokerHeaders.EventName] = "order-placed" });

    private OutgoingBrokerMessage Message() => Message(_topic);

    private async Task<TransportHost> StartHostAsync(IBrokerReceivePipeline pipeline)
    {
        var settings = new MessageBusSettings { Provider = MessageBusProviders.PostgreSql, ConcurrentMessageLimit = 4 };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MessageBus:PostgreSql:Schema"] = _schema })
            .Build();
        var topology = new BrokerTopology(
            "demo",
            _queue,
            [new BrokerSubscription(_topic, typeof(object), _topic, BrokerConsumerKind.Current)]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pipeline);
        new PostgreSqlBrokerTransport(options => options with { Lease = Lease, RenewInterval = RenewInterval, PollInterval = PollInterval })
            .Register(services, new BrokerTransportContext(settings, fixture.ConnectionString, configuration, topology));

        var host = new TransportHost(services.BuildServiceProvider());
        await host.StartAsync();
        return host;
    }

    private async Task WaitUntilBoundAsync()
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            try
            {
                if (await CountAsync("subscriptions") > 0)
                {
                    return;
                }
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal)
                || string.Equals(ex.SqlState, PostgresErrorCodes.InvalidSchemaName, StringComparison.Ordinal))
            {
                // The migration has not run yet.
            }

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the receiver should bind its queue");
            await Task.Delay(50, Ct);
        }
    }

    private async Task WaitUntilSettledAsync()
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (await CountAsync("messages") > 0)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the completed row should be deleted");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<(int DeliveryCount, bool Held)> ReadClaimAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = dataSource.CreateCommand(
            $"SELECT delivery_count, locked_until > now() FROM \"{_schema}\".messages WHERE queue = @queue");
        command.Parameters.AddWithValue("queue", _queue);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue("the row stays in the queue until it is completed");
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    private async Task<long> CountAsync(string table)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = dataSource.CreateCommand($"SELECT count(*) FROM \"{_schema}\".{table} WHERE queue = @queue");
        command.Parameters.AddWithValue("queue", _queue);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A pipeline whose every delivery takes <paramref name="hold"/>, counting deliveries.</summary>
    private sealed class SlowPipeline(TimeSpan hold) : IBrokerReceivePipeline
    {
        private int _deliveries;

        public int Deliveries => Volatile.Read(ref _deliveries);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deliveries);
            Entered.TrySetResult();
            await Task.Delay(hold, cancellationToken);
            Finished.TrySetResult();
            return BrokerDisposition.Complete;
        }
    }

    /// <summary>A started host: the provider and its hosted services, stopped in reverse on dispose.</summary>
    private sealed class TransportHost(ServiceProvider provider) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];

        public IBrokerPublisher Publisher => provider.GetRequiredService<IBrokerPublisher>();

        public async Task StartAsync()
        {
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(Ct);
                _started.Add(service);
            }
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = _started.Count - 1; i >= 0; i--)
            {
                await _started[i].StopAsync(CancellationToken.None);
            }

            await provider.DisposeAsync();
        }
    }
}
