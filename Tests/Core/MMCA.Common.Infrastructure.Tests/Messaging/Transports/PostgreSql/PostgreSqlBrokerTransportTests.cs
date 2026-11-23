using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>
/// The PostgreSQL transport's registration: a required connection string, the <c>MessageBus:PostgreSql</c>
/// keys, the migrator ahead of the receiver, and no receiver for a host that consumes nothing. Nothing
/// here opens a connection.
/// </summary>
public sealed class PostgreSqlBrokerTransportTests
{
    private const string ConnectionString = "Host=localhost;Database=broker;Username=u;Password=p";

    private static readonly BrokerSubscription Subscription =
        new("order-placed", typeof(object), "demo-order-placed", BrokerConsumerKind.Current);

    private static BrokerTransportContext Context(
        string? connectionString = ConnectionString,
        IReadOnlyDictionary<string, string?>? keys = null,
        IReadOnlyList<BrokerSubscription>? subscriptions = null) => new(
        new MessageBusSettings { Provider = MessageBusProviders.PostgreSql },
        connectionString,
        new ConfigurationBuilder().AddInMemoryCollection(keys ?? new Dictionary<string, string?>()).Build(),
        new BrokerTopology("demo", "demo-billing", subscriptions ?? []));

    private static ServiceProvider Register(BrokerTransportContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IBrokerReceivePipeline>());
        new PostgreSqlBrokerTransport().Register(services, context);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Transport_IsNamedPostgreSql_WithNoDelayCap()
    {
        var transport = new PostgreSqlBrokerTransport();

        transport.Name.Should().Be(MessageBusProviders.PostgreSql);
        transport.MaxRedeliveryDelay.Should().BeNull("visible_after can be pushed out by any interval");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Register_WithoutAConnectionString_ThrowsAtStartup(string? connectionString)
    {
        var act = () => new PostgreSqlBrokerTransport().Register(new ServiceCollection(), Context(connectionString));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'PostgreSql'*no broker connection string*MessageBus:ConnectionString*");
    }

    [Fact]
    public void Register_ABadSchemaName_ThrowsAtStartup()
    {
        var act = () => new PostgreSqlBrokerTransport().Register(
            new ServiceCollection(),
            Context(keys: new Dictionary<string, string?> { ["MessageBus:PostgreSql:Schema"] = "bad-name" }));

        act.Should().Throw<InvalidOperationException>().WithMessage("MessageBus:PostgreSql:Schema*");
    }

    [Fact]
    public async Task Register_WithSubscriptions_AddsThePublisher_ThenTheMigratorAheadOfTheReceiver()
    {
        await using var provider = Register(Context(subscriptions: [Subscription]));

        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<PostgreSqlPublisher>();
        provider.GetServices<IHostedService>().Select(s => s.GetType()).Should().Equal(typeof(PostgreSqlSchemaMigrator), typeof(PostgreSqlReceiver));
    }

    [Fact]
    public async Task Register_WithoutSubscriptions_StillMigrates_ButAddsNoReceiver()
    {
        await using var provider = Register(Context());

        provider.GetServices<IHostedService>().Select(s => s.GetType()).Should().Equal(typeof(PostgreSqlSchemaMigrator));
    }

    [Fact]
    public async Task Register_MigrateOnStartupOff_AddsNoMigrator()
    {
        await using var provider = Register(Context(
            keys: new Dictionary<string, string?> { ["MessageBus:PostgreSql:MigrateOnStartup"] = "false" },
            subscriptions: [Subscription]));

        provider.GetServices<IHostedService>().Select(s => s.GetType()).Should().Equal(typeof(PostgreSqlReceiver));
    }

    [Fact]
    public void Register_AContainerDisposedSynchronously_DisposesTheStore()
    {
        var provider = Register(Context(subscriptions: [Subscription]));
        provider.GetRequiredService<IBrokerPublisher>();
        provider.GetServices<IHostedService>().Should().HaveCount(2);

        var act = provider.Dispose;

        act.Should().NotThrow("a host or test that disposes its container without await must not fail on the transport");
    }

    [Fact]
    public void ReadSettings_Defaults_AreTheTransportSchemaAndRole()
    {
        var settings = PostgreSqlBrokerTransport.ReadSettings(new ConfigurationBuilder().Build());

        settings.EffectiveSchema.Should().Be("transport");
        settings.EffectiveRole.Should().Be("transport");
        settings.MigrateOnStartup.Should().BeTrue();
    }

    [Fact]
    public void ReadSettings_BindsTheSection()
    {
        var settings = PostgreSqlBrokerTransport.ReadSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:PostgreSql:Schema"] = "broker",
                ["MessageBus:PostgreSql:Role"] = "broker_role",
                ["MessageBus:PostgreSql:MigrateOnStartup"] = "false",
            })
            .Build());

        settings.EffectiveSchema.Should().Be("broker");
        settings.EffectiveRole.Should().Be("broker_role");
        settings.MigrateOnStartup.Should().BeFalse();
    }

    [Fact]
    public void Headers_RoundTripThroughTheJsonColumn_KeepingKeyCase()
    {
        var headers = new Dictionary<string, string>
        {
            ["mmca-event-name"] = "order-placed",
            ["MMCA-UserId"] = "42",
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };

        var read = PostgreSqlMessageStore.DeserializeHeaders(PostgreSqlMessageStore.SerializeHeaders(headers));

        read.Should().BeEquivalentTo(headers);
        read.ContainsKey("mmca-userid").Should().BeFalse("keys compare ordinally");
    }

    [Fact]
    public async Task Publisher_WaitsForTheMigration_WhenThisHostMigrates()
    {
        var store = new FakePostgreSqlMessageStore();
        var message = new OutgoingBrokerMessage("demo-order-placed", "order-placed", Guid.NewGuid(), new byte[] { 1 }, new Dictionary<string, string>());

        await new PostgreSqlPublisher(store, migrateOnStartup: true).PublishAsync(message, TestContext.Current.CancellationToken);
        await new PostgreSqlPublisher(store, migrateOnStartup: false).PublishAsync(message, TestContext.Current.CancellationToken);

        store.SchemaCalls.Should().Be(1);
        store.Published.Should().HaveCount(2);
    }
}
